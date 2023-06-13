using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Diagnostics;
using System.ComponentModel;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

// N.B. At runtime, this namespace name is updated to include a random string, so that the
// code can be loaded multiple times in a single session, when the code has been changed.
namespace ConsoleBouncer
{
    public static class NativeMethods
    {
        [DllImport( "kernel32.dll" )]
        public static extern ulong GetTickCount64();

        [DllImport( "kernel32.dll" )]
        public static extern uint GetCurrentThreadId();

        [DllImport( "kernel32.dll" )]
        public static extern int GetCurrentProcessId();

        [DllImport( "kernel32.dll", SetLastError = true, EntryPoint = "GetConsoleProcessList" )]
        private static extern uint native_GetConsoleProcessList( [In, Out] uint[] lpdwProcessList,
                                                                 uint dwProcessCount);

        public static uint[] GetConsoleProcessList()
        {
            int size = 100;
            uint[] pids = new uint[ size ];
            uint numPids = native_GetConsoleProcessList( pids, (uint) size );

            if( numPids > size )
            {
                size = (int) numPids + 10; // a lil' extra
                pids = new uint[ size ];
                numPids = native_GetConsoleProcessList( pids, (uint) size );
            }

            // TODO: should we just ignore it? Gracefully fail (don't install a handler)
            // if we get an empty list? What happens in a bg job?
            if( 0 == numPids )
            {
                throw new Win32Exception(); // uses GetLastError()
            }

            Array.Resize(ref pids, (int) numPids);
            return pids;
        }

        [DllImport( "kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "GetConsoleAliasW" )]
        private static extern int native_GetConsoleAlias( string lpSource,
                                                          StringBuilder lpTargetBuffer,
                                                          int TargetBufferLength,
                                                          string lpExeName );

        public static string GetConsoleAlias( string alias, string exeName )
        {
            var sb = new StringBuilder( 1028 );
            int result = native_GetConsoleAlias( alias, sb, sb.Capacity, exeName );
            // N.B. we are ignoring the return value; if the alias doesn't exist, we don't
            // distinguish that from "empty".
            return sb.ToString();
        }

        [DllImport( "kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "AddConsoleAliasW" )]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool native_AddConsoleAlias(string Source, string Target, string ExeName);

        public static void AddConsoleAlias( string alias, string aliasValue, string exeName )
        {
            if( !native_AddConsoleAlias( alias, aliasValue, exeName ) )
            {
                throw new Win32Exception(); // uses GetLastError()
            }
        }

        [DllImport( "kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "GetConsoleAliasesLengthW" )]
        private static extern int native_GetConsoleAliasesLength( string lpExeName );

        [DllImport( "kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "GetConsoleAliasesW" )]
        private static extern int native_GetConsoleAliases( [Out] char[] lpAliasBuffer, //StringBuilder lpAliasBuffer,
                                                            int AliasBufferLength,
                                                            string lpExeName );

        public static List< Tuple< string, string > > GetConsoleAliases( string exeName )
        {
            var result = new List< Tuple< string, string > >();

            int len = native_GetConsoleAliasesLength( exeName );

            if( len == 0 )
            {
                return result;
            }

            // Q: Why not use a StringBuilder?
            // A: The buffer gets filled with one big long string that contains embedded
            //    nulls (to separate each alias key/value pair). StringBuilder just really
            //    doesn't want to "see" past that first null, so we need to take things
            //    into our own hands.
            char[] buf = new char[ len ];
            int ret = native_GetConsoleAliases( buf, len, exeName );

            if( 0 == ret )
            {
                throw new Win32Exception(); // uses GetLastError()
            }

            int startIdx = 0;
            int zeroIdx = 0;

            // "The format of the data is as follows:
            //
            //     Source1=Target1\0Source2=Target2\0... SourceN=TargetN\0
            //
            // where N is the number of console aliases defined."
            //
            // (And there's actually an additional null at the end.)
            while( zeroIdx < buf.Length )
            {
                if( buf[ zeroIdx ] == ((char) 0) )
                {
                    if( zeroIdx == startIdx )
                    {
                        // double null terminator means it's the end
                        break;
                    }

                    // We're playing pretty fast and loose here (no validation)... that's
                    // okay; this code is really only for debugging, and if it blows up,
                    // the shell will handle the exception.
                    var chunk = new String( buf, startIdx, zeroIdx - startIdx );
                    int eqIdx = chunk.IndexOf( '=' );
                    var name = chunk.Substring( 0, eqIdx );
                    var val = chunk.Substring( eqIdx + 1 );
                    result.Add( new Tuple< string, string >( name, val ) );

                    startIdx = zeroIdx + 1;
                }
                zeroIdx++;
            }

            return result;
        }

        [DllImport( "kernel32.dll", SetLastError = true, EntryPoint = "GetStdHandle" )]
        private static extern IntPtr native_GetStdHandle( int handleId );

        [DllImport( "kernel32.dll", SetLastError = true, EntryPoint = "GetConsoleMode" )]
        [return: MarshalAs( UnmanagedType.Bool )]
        private static extern bool native_GetConsoleMode( IntPtr hConsoleHandle, out uint dwMode );

        [DllImport( "kernel32.dll", SetLastError = true, EntryPoint = "SetConsoleMode" )]
        [return: MarshalAs( UnmanagedType.Bool )]
        private static extern bool native_SetConsoleMode( IntPtr hConsoleHandle, uint dwMode );

        // Returns the current mode, or throws a Win32Exception on failure.
        public static uint GetConsoleMode( bool input = false )
        {
            var handle = native_GetStdHandle( input ? -10 : -11 );
            uint mode;
            if( native_GetConsoleMode( handle, out mode ) )
            {
                return mode;
            }
            throw new Win32Exception(); // uses GetLastError()
        }

        // Returns the new mode, or throws a Win32Exception on failure.
        public static uint SetConsoleMode( bool input, uint mode )
        {
            var handle = native_GetStdHandle( input ? -10 : -11 );
            if( native_SetConsoleMode(handle, mode ) )
            {
                return GetConsoleMode( input );
            }
            throw new Win32Exception(); // uses GetLastError()
        }

        [Flags()]
        enum ConsoleModeOutputFlags
        {
            ENABLE_PROCESSED_OUTPUT            = 0x0001,
            ENABLE_WRAP_AT_EOL_OUTPUT          = 0x0002,
            ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004,
            DISABLE_NEWLINE_AUTO_RETURN        = 0x0008,
            ENABLE_LVB_GRID_WORLDWIDE          = 0x0010,
        }

        public enum ConsoleBreakSignal : uint
        {
            CtrlC     = 0,
            CtrlBreak = 1,
            Close     = 2,
            Logoff    = 5,   // only received by services
            Shutdown  = 6,   // only received by services
        }

        [return: MarshalAs( UnmanagedType.Bool )]
        public delegate bool HandlerRoutine( ConsoleBreakSignal ctrlType );

        [return: MarshalAs( UnmanagedType.Bool )]
        [DllImport( "kernel32.dll", SetLastError = true )]
        public static extern bool SetConsoleCtrlHandler( HandlerRoutine handler,
                                                         [MarshalAs( UnmanagedType.Bool )] bool add );

        [DllImport( "kernel32.dll", SetLastError = true )]
        public static extern IntPtr GetConsoleWindow();


        public enum TaskbarStates
        {
            NoProgress    = 0,
            Indeterminate = 0x1,
            Normal        = 0x2,
            Error         = 0x4,
            Paused        = 0x8,
        }

        internal static class TaskbarProgress
        {
            [ComImport()]
            [Guid( "ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf" )]
            [InterfaceType( ComInterfaceType.InterfaceIsIUnknown )]
            private interface ITaskbarList3
            {
                // ITaskbarList
                [PreserveSig]
                int HrInit();

                [PreserveSig]
                int AddTab( IntPtr hwnd );

                [PreserveSig]
                int DeleteTab( IntPtr hwnd );

                [PreserveSig]
                int ActivateTab( IntPtr hwnd );

                [PreserveSig]
                int SetActiveAlt( IntPtr hwnd );

                // ITaskbarList2
                [PreserveSig]
                int MarkFullscreenWindow( IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fFullscreen );

                // ITaskbarList3
                [PreserveSig]
                int SetProgressValue( IntPtr hwnd, UInt64 ullCompleted, UInt64 ullTotal );

                [PreserveSig]
                int SetProgressState( IntPtr hwnd, TaskbarStates state );

                // N.B. we've left out the rest of the ITaskbarList3 methods...
            }

            [ComImport()]
            [Guid( "56fdf344-fd6d-11d0-958a-006097c9a090" )]
            [ClassInterface( ClassInterfaceType.None )]
            private class TaskbarInstance
            {
            }

            private static ITaskbarList3 s_taskbarInstance;

            private static ITaskbarList3 Instance
            {
                get
                {
                    if( null == s_taskbarInstance )
                    {
                        s_taskbarInstance = (ITaskbarList3) new TaskbarInstance();
                    }
                    return s_taskbarInstance;
                }
            }

            public static int SetProgressState(IntPtr windowHandle, TaskbarStates taskbarState)
            {
                return Instance.SetProgressState(windowHandle, taskbarState);
            }

            public static int SetProgressValue(IntPtr windowHandle, int progressValue, int progressMax)
            {
                return Instance.SetProgressValue(windowHandle, (ulong) progressValue, (ulong) progressMax);
            }
        }

        internal static bool ItLooksLikeWeAreInTerminal()
        {
            return !String.IsNullOrEmpty( Environment.GetEnvironmentVariable( "WT_SESSION" ) );
        }


        /// <summary>
        ///    Installs a control key handler which handles special signals like CTRL-C.
        /// </summary>
        /// <remarks>
        ///    N.B. Be careful about blocking the CTRL-handler thread. The handler is
        ///    dispatched on essentially a random threadpool thread, and a lock is held while
        ///    dispatching. So if you block the CTRL-handler thread inside your handler, and
        ///    somebody else tries to install a CTRL handler on a different thread (which your
        ///    handler is dependent on), you will deadlock.
        /// </remarks>
        public sealed class CtrlCInterceptor : IDisposable
        {
            private HandlerRoutine m_handler;
            private HandlerRoutine m_handlerWrapper;
            private bool m_allowExtendedEvents;

            private bool _HandlerWrapper( ConsoleBreakSignal ctrlType )
            {
                if( !m_allowExtendedEvents )
                {
                    // There are actually other signals which are not represented by the
                    // ConsoleSpecialKey type (like CTRL_CLOSE_EVENT). We won't handle
                    // those.
                    if( (ctrlType != ConsoleBreakSignal.CtrlBreak) &&
                        (ctrlType != ConsoleBreakSignal.CtrlC) )
                    {
                        return false;
                    }
                }
                return m_handler( ctrlType );
            } // end _HandlerWrapper

            /// <summary>
            ///    Installs a control key handler which handles special signals like CTRL-C.
            /// </summary>
            /// <remarks>
            ///    N.B. Be careful about blocking the CTRL-handler thread. The handler is
            ///    dispatched on essentially a random threadpool thread, and a lock is held
            ///    while dispatching. So if you block the CTRL-handler thread inside your
            ///    handler, and somebody else tries to install a CTRL handler on a different
            ///    thread (which your handler is dependent on), you will deadlock.
            /// </remarks>
            public CtrlCInterceptor( HandlerRoutine replacementHandler )
                : this( replacementHandler, false )
            {
            }

            public CtrlCInterceptor( HandlerRoutine replacementHandler, bool allowExtendedEvents )
            {
                if( null == replacementHandler )
                    throw new ArgumentNullException( "replacementHandler" );

                m_handler = replacementHandler;
                m_handlerWrapper = _HandlerWrapper;
                m_allowExtendedEvents = allowExtendedEvents;

                if( !NativeMethods.SetConsoleCtrlHandler( m_handlerWrapper, true ) )
                {
                    throw new Win32Exception(); // automatically uses last win32 error
                }
            } // end constructor

            public void Dispose()
            {
                if( !NativeMethods.SetConsoleCtrlHandler( m_handlerWrapper, false ) )
                {
                    // TODO: normally you don't want to throw from Dispose, so maybe I should
                    // just assert...
                    throw new Win32Exception(); // automatically uses last win32 error
                }
            } // end Dispose()
        } // end class CtrlCInterceptor


        private static ConsoleBouncerImpl s_theBouncer;

        public static ConsoleBouncerImpl InstallBouncer()
        {
            if( s_theBouncer != null )
            {
                throw new InvalidOperationException();
            }

            s_theBouncer = new ConsoleBouncerImpl();
            return s_theBouncer;
        }

        public class ConsoleBouncerImpl : IDisposable
        {
            private CtrlCInterceptor m_ctrlCInterceptor;
            private uint[] m_allowedPids;

            // At one point I tried to be cute and use fancier kaomoji... but despite
            // setting the console output encoding to UTF8, there were still encoding
            // problems (like in legacy powershell.exe), so we'll just stick to ASCII.
            private const string TheBouncer = "(* ^_^)";

            // Certain data is shared between all ConsoleBouncers that are connected to
            // the same console. We do this by [ab]using console "aliases".
            //
            // Console aliases, for our purposes, are key-value pairs, grouped by "EXE"
            // (See the documentation for GetConsoleAlias/AddConsoleAlias for more info.)
            // We don't have a real EXE... we just coopt this mechanism in order to store
            // data that can be retrieved from any process attached to the console. We use
            // a named mutex to serialize access to the aliases. The name of the mutex is
            // stored as a console alias, naturally (if the alias has not been set yet, we
            // assume we are the first ConsoleBouncer attached to the console, and we get
            // to pick the mutex name and create it).
            //
            // Pro tip: you can use "doskey /macros:ALL" to dump all the aliases. If there
            // are other EXEs that have populated a bunch of aliases, it is not convenient
            // to get to ours because of the spaces in the name... in that case, you can
            // use our private DumpAliases function:
            //
            //      & (gmo ConsoleBouncer) { DumpAliases }

            // This string is used as the EXE name "key" for data that we store as console
            // aliases.
            private const string ExeNameForConsoleAliases = TheBouncer + " ConsoleBouncer";

            private const string CookieTreeName = "CurrentBouncerCookieTree";
            private const string VerbosityName = "Verbosity";
            private const string MutexNameName = "MutexName";
            private const string AllowedProcessesName = "AllowedProcesses";
            private const string GracePeriodMillisName = "GracePeriodMillis";
            private const string CustomizedSettingsName = "CustomizedSettings";
            private const string DisabledName = "Disabled";
            private const string DisarmedName = "DisarmedBy";
            private const string ClearProgressName = "ClearProgress";
            private const string MaxRoundsName = "MaxRounds";

            // Synchronizes access to data stored in console aliases.
            private Mutex m_consoleMutex;

            // "Cookies" and the "cookie tree":
            //
            // Among a set of processes attached to a given console, there may be multiple
            // ConsoleBouncers. For example, consider the following process tree:
            //
            //      cmd.exe (PID 12)
            //        \
            //        powershell.exe (PID 34)
            //          \
            //          pwsh.exe (PID 56)
            //
            // Both powershell.exe (PID 34) and pwsh.exe (PID 56) may have a
            // ConsoleBouncer. The ConsoleBouncer ctrl+c handler needs to behave
            // differently in each of those processes: only the leaf-most should terminate
            // console-attached processes not in its allow list (if PID 56 booted
            // processes, it would terminate pwsh.exe); and non-leaf-most handlers should
            // return TRUE from their handler, to prevent other handlers from running.
            //
            // So how do we decide which ConsoleBouncer is the one "in charge"? (Which is
            // the leaf-most?) Each ConsoleBouncer has a unique cookie (based on its PID),
            // and we store a list of these cookies as a console alias. The list is stored
            // in reverse of how you might normally think of building a list: the
            // leaf-most cookie is the first thing in "cookie tree" value. So when a
            // ConsoleBouncer ctrl+c handler runs, it checks the cookie tree, and if its
            // own cookie is first, then it knows that it is The Boss, and the other
            // bouncers know to stay cool.

            private string m_myCookie;

            private static string _ReadSharedValue( string valueName )
            {
                return GetConsoleAlias( valueName, ExeNameForConsoleAliases );
            }

            private static void _WriteSharedValue( string valueName, string value )
            {
                AddConsoleAlias( valueName, value, ExeNameForConsoleAliases );
            }

            private static int s_defaultVerbosity = 1;

            private int m_cachedVerbosity = -1;

            private int _ReadPersistedVerbosity()
            {
                string verbosityStr = _ReadSharedValue( VerbosityName );

                if( String.IsNullOrEmpty( verbosityStr ) )
                {
                    return s_defaultVerbosity;
                }

                int verbosity = 0;
                if( !Int32.TryParse( verbosityStr, out verbosity ) )
                {
                    verbosity = -1;
                }

                return verbosity;
            }

            private void _RefreshCachedVerbosity()
            {
                m_cachedVerbosity = _ReadPersistedVerbosity();
            }

            public int Verbosity
            {
                get
                {
                    if( m_cachedVerbosity < 0 )
                    {
                        _RefreshCachedVerbosity();
                    }

                    return m_cachedVerbosity;
                }

                set
                {
                    m_cachedVerbosity = value;
                    _WriteSharedValue( VerbosityName, value.ToString() );
                }
            }

            internal void Say(int msgVerbosity, string fmt, params object[] inserts)
            {
                if( msgVerbosity > Verbosity )
                    return;

                Console.WriteLine( "  {0} {1}: {2}", TheBouncer, NativeMethods.GetCurrentProcessId(), String.Format( fmt, inserts ) );
            }

            private static char[] s_semiArray = new char[] { ';' };

            // This is a list of process names (without the ".exe" extension) that we do
            // *not* kill, even if they are not in the m_allowedPids list.
            public string[] AllowedProcesses
            {
                get
                {
                    return _ReadSharedValue( AllowedProcessesName ).Split( s_semiArray, StringSplitOptions.RemoveEmptyEntries );
                }

                set
                {
                    _WriteSharedValue( AllowedProcessesName, String.Join( ";", value ) );
                }
            }

            private const int c_DefaultGraceMillis = 1000;

            // How long to wait before terminating processes that are not in the
            // m_allowedPids list.
            //
            // You don't want this to be very long... processes that end up as lingering
            // processes probably got attached to the console after the original ctrl+c,
            // so they are probably NOT endeavoring to exit; they are probably just
            // proceeding as normal, without a care in the world; so in those cases, we
            // will likely always end up waiting for the entire grace period.
            public int GracePeriodMillis
            {
                get
                {
                    int millis;
                    if( !Int32.TryParse( _ReadSharedValue( GracePeriodMillisName ), out millis ) )
                    {
                        return c_DefaultGraceMillis;
                    }
                    return millis;
                }

                set
                {
                    if( value < 0 )
                    {
                        // Do not allow infinite waits.
                        value = c_DefaultGraceMillis;
                    }
                    else if( value > 60000 )
                    {
                        // ... or waits that *seem* infinite.
                        value = c_DefaultGraceMillis;
                    }

                    _WriteSharedValue( GracePeriodMillisName, value.ToString() );
                }
            }

            // Keeps track of which settings have been altered from default.
            public string[] CustomizedSettings
            {
                get
                {
                    return _ReadSharedValue( CustomizedSettingsName ).Split( s_semiArray, StringSplitOptions.RemoveEmptyEntries );
                }

                set
                {
                    _WriteSharedValue( CustomizedSettingsName, String.Join( ";", value ) );
                }
            }

            // Indicates that we are completely disabled. Our handler is still installed,
            // but it will simply return false, allowing other handlers to run, without
            // taking any action.
            public bool Disabled
            {
                // It is not documented/used in our public interface, but Disabled can be
                // either a 0, 1, or the PID of a process that disabled us. If the latter,
                // then we auto-reenable when that process has exited.
                get
                {
                    string disabledStr = _ReadSharedValue( DisabledName );
                    if( String.IsNullOrEmpty( disabledStr ) )
                    {
                        return false;
                    }

                    int val;
                    if( Int32.TryParse( disabledStr, out val ) )
                    {
                        if( val == 0 )
                        {
                            return false;
                        }
                        else if( val == 1 )
                        {
                            return true;
                        }
                        else
                        {
                            // "Disabled By": we remain disabled while the specified
                            // process is still around.
                            try
                            {
                                using( Process proc = Process.GetProcessById( val ) )
                                {
                                    if( !proc.HasExited )
                                    {
                                        return true;
                                    }
                                }
                            }
                            catch( Exception e )
                            {
                                // Ignore it: could be gone
                                Say( 2, "   disabled-by proc {0} is gone...", val );
                                Say( 3, "      (exception was: {0}: {1})", e.GetType().Name, e.Message );
                            }
                            _WriteSharedValue( DisabledName, null );
                            return false;
                        }
                    }
                    else
                    {
                        // We couldn't parse the string as an int... well, whatever it is,
                        // we'll assume it means we are disabled.
                        return true;
                    }
                }

                set
                {
                    if( value )
                    {
                        _WriteSharedValue( DisabledName, "1" );
                    }
                    else
                    {
                        _WriteSharedValue( DisabledName, null );
                    }
                }
            }

            // Indicates that we are temporarily semi-disabled, until rearmed or the pid
            // specified by this property exits. The handler will run as normal (with
            // non-leaf bouncers masking other handlers), but the leaf-most bouncer (the
            // one "in charge") won't actually kill any processes.
            public int DisarmedBy
            {
                // The DisarmedBy value can be either a 0, 1, or the PID of a process that
                // disarmed us. If the latter, then we auto-reenable when that process has
                // exited.
                get
                {
                    string disarmedStr = _ReadSharedValue( DisarmedName );
                    if( String.IsNullOrEmpty( disarmedStr ) )
                    {
                        return 0;
                    }

                    int val;
                    if( Int32.TryParse( disarmedStr, out val ) )
                    {
                        if( (val == 0) || (val == 1) )
                        {
                            return val;
                        }
                        else
                        {
                            // "Disarmed By": we remain semi-disabled while the specified
                            // process is still around.
                            try
                            {
                                using( Process proc = Process.GetProcessById( val ) )
                                {
                                    if( !proc.HasExited )
                                    {
                                        return val;
                                    }
                                }
                            }
                            catch( Exception e )
                            {
                                // Ignore it: could be gone
                                Say( 2, "   disarmed-by proc {0} is gone...", val );
                                Say( 3, "      (exception was: {0}: {1})", e.GetType().Name, e.Message );
                            }
                            _WriteSharedValue( DisarmedName, null );
                            return 0;
                        }
                    }
                    Say( 0, "Error: how did we get a DisarmedBy value of {0}?", disarmedStr );
                    return 0;
                }

                set
                {
                    string valStr = value == 0 ? null : value.ToString();
                    _WriteSharedValue( DisarmedName, valStr );
                }
            }

            public bool ClearProgress
            {
                get
                {
                    string strVal = _ReadSharedValue( ClearProgressName );

                    if( String.IsNullOrEmpty( strVal ) )
                    {
                        return true;
                    }

                    int val = 0;
                    if( !Int32.TryParse( strVal, out val ) )
                    {
                        return true;
                    }

                    return val != 0;
                }

                set
                {
                    _WriteSharedValue( ClearProgressName, value ? "1" : "0" );
                }
            }

            // The maximum number of passes the bouncer should take when clearing out
            // non-allowed processes.
            //
            // The theory for why more than one round might be needed is that the same
            // race between process creation and console attachment that could cause
            // lingering processes in the first place could cause us to need a second
            // round of cleanup... but I've never actually seen more than one round be
            // needed. Probably because the process that is spawing processes got taken
            // out with the original ctrl+c signal. So we'll stick to a default of just
            // one round for now, with the possibility to expand to more, if needed.
            public int MaxRounds
            {
                get
                {
                    string strVal = _ReadSharedValue( MaxRoundsName );

                    if( String.IsNullOrEmpty( strVal ) )
                    {
                        return 1;
                    }

                    int val = 0;
                    if( !Int32.TryParse( strVal, out val ) )
                    {
                        return 1;
                    }

                    return val > 0 ? val : 1;
                }

                set
                {
                    _WriteSharedValue( MaxRoundsName, value.ToString() );
                }
            }

            // Just a handy IDisposable wrapper for a mutex.
            private class LockedMutex : IDisposable
            {
                private Mutex m_mutex;

                public LockedMutex( Mutex m )
                {
                    m_mutex = m;
                    m_mutex.WaitOne();
                }

                public void Dispose()
                {
                    m_mutex.ReleaseMutex();
                }
            }

            private IDisposable LockTheMutex()
            {
                var theLock = new LockedMutex( m_consoleMutex );
                _RefreshCachedVerbosity();
                return theLock;
            }

            // When shells exit, they may not get a chance to clean up their cookie from
            // the cookie tree. So when we need to consult the cookie tree, we need to
            // always tidy it up first, and make sure bouncers listed in the tree are
            // still alive.
            private bool _IsBouncerStillAlive( string cookie )
            {
                if( cookie == m_myCookie )
                {
                    return true;
                }

                int bouncerPid = -1;
                try
                {
                    string[] tokens = cookie.Split( '-' );
                    bouncerPid = Int32.Parse( tokens[ 0 ] );
                    int bouncerDepth = Int32.Parse( tokens[ 1 ] );
                    using( Process bouncerProc = Process.GetProcessById( bouncerPid ) )
                    {
                        return !bouncerProc.HasExited;
                    }
                }
                catch( Exception e )
                {
                    // Ignore it: other proc is gone; check the next one.
                    Say( 2, "   Bouncer Proc {0} is gone...", bouncerPid );
                    Say( 3, "      (exception was: {0}: {1})", e.GetType().Name, e.Message );
                }
                return false;
            }

            // Reads the cookie tree, hauls out any dead bouncers, and makes sure our
            // cookie is included in the tree.
            private List<string> _ParseAndPruneCookieTree(string rawTree, out bool alreadyIncludedMe )
            {
                string[] cookies = rawTree.Split( ';' );

                var resultTree = cookies.Where( _IsBouncerStillAlive ).ToList();

                alreadyIncludedMe = resultTree.Contains( m_myCookie );

                if( !alreadyIncludedMe )
                {
                    // (when first loaded, we aren't in the tree yet)
                    resultTree.Insert( 0, m_myCookie );
                }

                return resultTree;
            }

            // Checks if a process is either in the m_allowedPids list, or is one of the
            // AllowedProcesses.
            private bool _IsPidAllowedToStay( uint pid, HashSet<string> allowedProcNames )
            {
                if( m_allowedPids.Contains( pid ) )
                {
                    return true;
                }

                if( allowedProcNames.Count > 0 )
                {
                    try
                    {
                        using( Process proc = Process.GetProcessById( (int) pid ) )
                        {
                            if( allowedProcNames.Contains( proc.ProcessName ) )
                            {
                                return true;
                            }
                        }
                    }
                    catch( Exception e )
                    {
                        // Ignore it: could be gone
                        Say( 2, "   target proc {0} is gone...", pid );
                        Say( 3, "      (exception was: {0}: {1})", e.GetType().Name, e.Message );
                    }
                }

                return false;
            }

            // Calculates what processes need to be killed (populated into procsToBoot),
            // and returns the count.
            private int _GatherProcessesToBoot( HashSet< string > allowedProcNamesSet,
                                                List< Process > procsToBoot )
            {
                procsToBoot.Clear();

                // These are the processes currently attached to this console:
                uint[] curPids = NativeMethods.GetConsoleProcessList();

                foreach( var pid in curPids )
                {
                    if( !_IsPidAllowedToStay( pid, allowedProcNamesSet ) )
                    {
                        Process proc = null;
                        try
                        {
                            proc = Process.GetProcessById( (int) pid );
                        }
                        catch( ArgumentException )
                        {
                            // Ignore it: process could be gone, or something else that we
                            // likely can't do anything about it.
                        }

                        if( proc != null )
                        {
                            Say( 2, "You can leave on your own or I'll help you, {0}", pid );
                            procsToBoot.Add( proc );
                        }
                    }
                }
                return procsToBoot.Count;
            }

            private int _MillisLeftUntilDeadline( ulong deadline )
            {
                long diff = (long) (deadline - GetTickCount64());

                if( diff < 0 )
                {
                    diff = 0;
                }
                else if( diff >= (long) Int32.MaxValue )
                {
                    // Should not ever actually happen...
                    diff = c_DefaultGraceMillis;
                }

                return (int) diff;
            }

            // The "business end" of the bouncer...
            private void _BootProcessesThatAreNotOnTheAllowList( string[] allowedProcNames )
            {
                var procsToBoot = new List< Process >();

                var allowedProcNamesSet = new HashSet<string>( allowedProcNames,
                                                               StringComparer.OrdinalIgnoreCase );

                // If it takes more than a few rounds of cleanup, we are in some kind of
                // pathological situation, and we'll bow out.
                int round = 0;

                while( (round++ < MaxRounds) &&
                       (_GatherProcessesToBoot( allowedProcNamesSet, procsToBoot ) > 0) )
                {
                    // We'll give them up to GracePeriodMillis for them to exit on their
                    // own, in case they actually did receive the original ctrl+c, and are
                    // just a tad slow shutting down.
                    ulong deadline = GetTickCount64() + (ulong) GracePeriodMillis;

                    var notDeadYet = procsToBoot.Where(
                            (p) => !p.WaitForExit( _MillisLeftUntilDeadline( deadline ) ) );

                    foreach( var proc in notDeadYet )
                    {
                        try
                        {
                            // "Kill, kill, kill!" - Miss Hannigan
                            proc.Kill();
                        }
                        // Ignore problems; maybe it's gone already, maybe something else;
                        // whatever.
                        catch( InvalidOperationException ) { }
                        catch( Win32Exception ) { }
                    }

                    foreach( var proc in procsToBoot )
                    {
                        proc.Dispose();
                    }
                } // end retry loop

                if( ClearProgress )
                {
                    uint consoleMode = GetConsoleMode();
                    if( ItLooksLikeWeAreInTerminal() )
                    {
                        // We can use the [semi-]standard OSC sequence:
                        // https://conemu.github.io/en/AnsiEscapeCodes.html#ConEmu_specific_OSC
                        if( 0 != (consoleMode & (uint) ConsoleModeOutputFlags.ENABLE_VIRTUAL_TERMINAL_PROCESSING) )
                        {
                            Console.Write( "\x001b]9;4;0;0\a" );
                            Say( 3, "cleared progress via VT OSC sequence..." );
                        }
                    }
                    else
                    {
                        IntPtr hwnd = GetConsoleWindow();
                        if( hwnd != IntPtr.Zero )
                        {
                            int ret = TaskbarProgress.SetProgressState( hwnd, TaskbarStates.NoProgress );
                            Say( 3, "cleared progress via taskbar COM interface (returned: {0})", ret );
                        }
                    }
                }
            } // end _BootProcessesThatAreNotOnTheAllowList()

            // This is what gets called when ctrl+c is pressed.
            private bool _Handler( ConsoleBreakSignal ctrlType )
            {
                Say( 2, "CancelOnCtrlC handler called" );

                if( ctrlType == ConsoleBreakSignal.Close )
                {
                    Say( 2, "(it was a Close signal)" );
                    // We won't actually Dispose() ourselves, since somebody else may have
                    // a reference to us.
                    //
                    // And actually... in practice, I'm not sure if this ever gets called;
                    // and if it did, seems like there is a good chance the entire console
                    // is going away, so maybe this is pointless, but:
                    _RemoveSelfFromCookieTreeAndUninstallHandler();
                    return false; // let other handlers run
                }
                else if( (ctrlType == ConsoleBreakSignal.CtrlBreak) ||
                         (ctrlType == ConsoleBreakSignal.Logoff) ||
                         (ctrlType == ConsoleBreakSignal.Shutdown) )
                {
                    return false; // do nothing; let other handlers run
                }

                string[] allowedProcNames = null;

                // Am I the current bouncer?
                using( LockTheMutex() )
                {
                    if( Disabled )
                    {
                        Say( 2, "  (but we are disabled, so I'll stand aside)" );
                        return false; // allow other handlers to run
                    }

                    string rawCookieTree = _ReadSharedValue( CookieTreeName );

                    bool alreadyIncludedMe;
                    List< string > cookies = _ParseAndPruneCookieTree( rawCookieTree, out alreadyIncludedMe );

                    var updatedRawTree = String.Join( ";", cookies );

                    if( updatedRawTree.Length != rawCookieTree.Length )
                    {
                        _WriteSharedValue( CookieTreeName, updatedRawTree );
                    }

                    string leaf = cookies[ 0 ];

                    if( leaf != m_myCookie )
                    {
                        Say( 3, "(oh, it's not me)" );
                        return true; // nobody else needs to know about this event; handle it
                    }

                    // Oh, I'M the current bouncer. Alright; let's get to work.
                    allowedProcNames = AllowedProcesses;
                }

                if( DisarmedBy == 0 )
                {
                    Say( 2, "I'm the bouncer here; time to leave, folks..." );
                    _BootProcessesThatAreNotOnTheAllowList( allowedProcNames );
                }
                else
                {
                    Say( 2, "(I'm the bouncer, but I'm on break...)" );
                }

                // We should allow other handlers to run. For example, maybe there are no
                // child processes at all, and the user is just trying to cancel a normal
                // cmdlet/function.
                return false;
            }

            public ConsoleBouncerImpl()
            {
                m_allowedPids = NativeMethods.GetConsoleProcessList();

                string mutexName = _ReadSharedValue( MutexNameName );
                if( String.IsNullOrEmpty( mutexName ) )
                {
                    mutexName = String.Format( "ConsoleBouncer-Mutex-{0:x}-{1:x}", GetCurrentProcessId(), GetCurrentThreadId() );
                    _WriteSharedValue( MutexNameName, mutexName );
                }

                bool createdNew = false;
                m_consoleMutex = new Mutex( false, mutexName, out createdNew );

                if( createdNew )
                {
                    // We are the first bouncer... initialize hard-coded default-allowed
                    // processes. These are processes which actually handle ctrl+c (they
                    // do not exit when ctrl+c is pressed, nor do they disable standard
                    // ctrl+c handling).
                    AllowedProcesses = new string[] { "cdb", "kd" };
                }

                m_myCookie = String.Format( "{0}-{1}", GetCurrentProcessId(), m_allowedPids.Length );

                using( LockTheMutex() )
                {
                    string oldCookieTreeRaw = _ReadSharedValue( CookieTreeName );
                    string newTreeRaw = m_myCookie;
                    bool needToInstallHandler = true;

                    if( !String.IsNullOrEmpty( oldCookieTreeRaw ) )
                    {
                        bool alreadyIncludedMe;
                        var newTree = _ParseAndPruneCookieTree( oldCookieTreeRaw, out alreadyIncludedMe ); // will auto-include m_myCookie

                        if( alreadyIncludedMe )
                        {
                            // Oh, it's possible that somebody else has loaded a separate
                            // copy of the module into the current process. In which case,
                            // we will back off. Other than not setting up our own ctrl+c
                            // handler, we'll stick around, so that our caller can still
                            // use our exported commands (setting/getting options).
                            Say( 3, "Backing off due to existing bouncer in the current process..." );
                            needToInstallHandler = false;
                            // We leave m_ctrlCInterceptor null as an indication of this
                            // condition.
                        }
                        else
                        {
                            newTreeRaw = String.Join( ";", newTree );
                        }
                    }

                    if( needToInstallHandler )
                    {
                        m_ctrlCInterceptor = new CtrlCInterceptor( _Handler );

                        // Let's introduce ourselves.
                        string pidsPhrase = m_allowedPids.Length > 1
                                                ? String.Format( "allow these {0} pids", m_allowedPids.Length )
                                                : "only allow this process";

                        Say( 2, "Hi, I'm your console bouncer. I'll {0} to stay attached to the console when ctrl+c is pressed{1}",
                                pidsPhrase,
                                m_allowedPids.Length > 1 ? ":" : "." );

                        if( m_allowedPids.Length > 1 )
                        {
                            foreach( var pid in m_allowedPids )
                            {
                                Say( 2, "    {0}", pid );
                            }
                        }

                        _WriteSharedValue( CookieTreeName, newTreeRaw );
                    }
                }
            } // end constructor

            private void _RemoveSelfFromCookieTreeAndUninstallHandler()
            {
                using( LockTheMutex() )
                {
                    // If m_ctrlCInterceptor is null, that we means we are deferring to
                    // some other in-process bouncer, so we won't mess with the cookie
                    // tree here.
                    if( m_ctrlCInterceptor != null )
                    {
                        string curCookieTree = _ReadSharedValue( CookieTreeName );
                        string withoutMe = curCookieTree.Replace( m_myCookie, String.Empty ).Trim( ';' ).Replace( ";;", ";" );
                        _WriteSharedValue( CookieTreeName, withoutMe );
                    }
                }

                if( m_ctrlCInterceptor != null )
                {
                    m_ctrlCInterceptor.Dispose();
                    m_ctrlCInterceptor = null;
                }
            }

            public void Dispose()
            {
                Say( 2, "Disposing of the bouncer..." );
                _RemoveSelfFromCookieTreeAndUninstallHandler();

                m_consoleMutex.Dispose();
                s_theBouncer = null;
            }
        } // end class ConsoleBouncerImpl
    } // end class NativeMethods
}

