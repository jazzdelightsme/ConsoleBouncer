# ConsoleBouncer

The ConsoleBouncer module implements a <kbd>ctrl+c</kbd> handler to ensure child
processes do not linger (and mess up the console) when canceled.

## How to Install

There are two editions of PowerShell: legacy Windows PowerShell (5.1, `powershell.exe`),
and modern (current) PowerShell (v7+, `pwsh.exe`), and you should install in such a way
that ConsoleBouncer will be loaded into both.

**IMPORTANT**: if you do not follow these steps carefully and exactly, you can end up in a
situation where the ConsoleBouncer module is *not* loaded or available in one version of
PowerShell or the other. That would be bad, because if you have a process tree with both
`powershell.exe` and `pwsh.exe` in it, and the ConsoleBouncer is not loaded in one of your
shells, a ctrl+c can unexpectedly kill the other one, which you probably would not
appreciate. So take care with the following instructions:

1. Start an **elevated** *legacy* Windows 5.1 `powershell.exe` process.
2. `Install-Module ConsoleBouncer -Scope AllUsers`
3. Update **BOTH** your `$profile` scripts (for both versions of PS) to include the line “`Import-Module ConsoleBouncer`”. (Helper script that you can copy/paste is below.)

Step 1 uses **legacy** Windows PowerShell, because when you run `Install-Module -Scope
AllUsers` from there, the module gets installed into a location in Program Files that is
"visible" in the `$env:PSModulePath` for both legacy and modern PowerShell. This is better
than installing twice, one copy each for legacy and modern, not just to avoid having two
copies, but also to avoid version mismatch between them.

Here is code to automatically perform those steps; just open an **elevated** *legacy*
Windows 5.1 `powershell.exe` window and paste it in:

```powershell
try
{
    if( $PSVersionTable.PSVersion.Major -ne 5 )
    {
        throw "Please run this in **legacy** Windows PowerShell (powershell.exe)."
    }

    [bool] $isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

    if( !$isAdmin )
    {
        throw "Please run this in an *elevated* Windows PowerShell window."
    }

    Install-Module ConsoleBouncer -Scope AllUsers -ErrorAction Stop # follow the prompts

    $legacyProfile = '~\Documents\WindowsPowerShell\Microsoft.PowerShell_profile.ps1'
    $modernProfile = '~\Documents\PowerShell\Microsoft.PowerShell_profile.ps1'

    foreach( $p in @( $legacyProfile, $modernProfile ) )
    {
        if( !(Test-Path $p) )
        {
            mkdir -Force (Split-Path $p) | Out-Null
            Set-Content -Path $p -Value ''
        }
        if( !((Get-Content -Raw $p) -like "*Import-Module ConsoleBouncer*") )
        {
            Add-Content -Path $p -Value "`r`n`r`nImport-Module ConsoleBouncer`r`n"
        }
    }
}
catch
{
    Write-Error $_
}
```

## Details

Ctrl+c cancellation does not work really well on Windows. Each process attached to a
console receives a ctrl+c signal (as opposed to just the active shell), and *sometimes,*
when a shell has launched some large tree of child processes (imagine a build system, for
example), some processes do not exit (perhaps due to races between process creation and
console attachment), leaving multiple processes all concurrently trying to consume console
input, which Does Not Work Well(TM). It's usually not too bad when `cmd.exe` is your
shell, because you can just keep mashing on ctrl+c and usually get back to a usable state.
But it's considerably worse in PowerShell, because PSReadLine temporarily disables ctrl+c
signals when waiting at the prompt, and can be completely unrecoverable. (TBD: link to
PSReadLine Issue.)

The ConsoleBouncer module implements a ctrl+c handler for PowerShell shell processes
to mitigate this problem (it works in both legacy `powershell.exe` and `pwsh.exe`).

The way it works is that when it is loaded, it takes a look at which PIDs are
currently attached to the console (there could be multiple, for example, if you
launched PowerShell from cmd.exe), and remembers those PIDs as the "allowed PIDs".
Later, when a ctrl+c signal comes along, the ConsoleBouncer handler enumerates all
PIDs attached to the console, and kills any which are not in the allow list (after a
[configurable] grace period (default of 1 second)).

Note that it only kills processes that are attached to the console, so if you launched
some GUI processes after loading ConsoleBouncer (notepad, mspaint, etc.), they will
not be touched.

The ConsoleBouncer handles nested shell processes: only the ConsoleBouncer handler in
the "leaf-most" PowerShell shell process will terminate stray processes; and
non-leaf-most handlers will return `TRUE` from the handler, to signal that the event has
been handled, and other handlers should not run (thus hiding the ctrl+c from non-leaf
shells).

There are a few configurable options (grace period, verbosity of informational
messages, disabled/enabled), which can be configured or viewed with
`Get-ConsoleBouncerOption` and `Set-ConsoleBouncerOption`.

### Settings are shared among a console group

**N.B.** ConsoleBouncer settings are *shared* between all PowerShell shells (that have the
ConsoleBouncer module loaded) that share a console. So for the following process tree:

```
    cmd.exe (PID 12)
      \
      powershell.exe (PID 34)
        \
        pwsh.exe (PID 56)
```

If you change the grace period with "`Set-ConsoleBouncerOption -GracePeriodMillis
2000`") in `pwsh.exe` (PID 56), and then exit back to `powershell.exe` (PID 34), the grace
period will still be set to 2000 milliseconds.

### Recommended use pattern

If you are using ConsoleBouncer, you should load it from your `$profile` script, so that
it is available in every (PowerShell) shell process. If you customize any settings
(such as the grace period before termination) from your `$profile` script, use the
`-IfNotAlreadySet` switch, so that launching a child shell will not overwrite a runtime
customization of the setting in a parent shell.

#### Example:

In `C:\users\$env:USERNAME\Documents\PowerShell\Microsoft.PowerShell_profile.ps1`:
        
```powershell
    Import-Module ConsoleBouncer
    Set-ConsoleBouncer -GracePeriodMillis 500 -IfNotAlreadySet # optional
```

Note that legacy `powershell.exe` and modern `pwsh.exe` have different `$profile` script
paths (legacy is under `Documents\WindowsPowerShell`); you would want to have the same
code in both (or have both version-specific profile scripts dot-execute a common,
shared script).

### Mitigation - Excluding processes

Some programs may need to be excluded from the default "kill" behavior. For example,
the console debugger `cdb.exe` relies on handling ctrl+c (to break into the target). By
default, `cdb.exe` and `kd.exe` are not terminated by the ConsoleBouncer handler, and you
can add more such processes via "`Set-ConsoleBouncerOption -AllowedProcessname <foo>`"
(leave off the `.exe` extension).

### Mitigation - Temporarily disabling ConsoleBouncer

If you run into some other situation where you need to temporariy disable
ConsoleBouncer, simply unloading the ConsoleBouncer module is not a good solution,
since it could be loaded in a parent shell process (and then your current shell, where
you've just unloaded ConsoleBouncer, would be subject to termination when the user
types ctrl+c). Instead, to temporarily disable ConsoleBouncer, you should run either:

```powershell
    Set-ConsoleBouncerOption -Disarm
```
or
```powershell
    Set-ConsoleBouncerOption -Disable
```

The first option is the "softer" / safer option: the handler will still run, and mask
off ctrl+c signals in non-leaf shells; but it will not terminate stray processes, and
ConsoleBouncer will be automatically re-armed when the shell that disarmed it exits.

The second option is a bit stronger: it stays disabled (in all PowerShell shells
attached to the current console) until reenabled, and though the handler runs, it just
lets the ctrl+c signal pass through (as if it weren't there) in all processes that
ConsoleBouncer is loaded in.

### Weaknesses

1. **Reliance on PIDs.** The `GetConsoleProcessList` API only reports process IDs (PIDs), but
   PIDs can be reused, so it's possible that in between querying `GetConsoleProcessList` and
   opening handles to the processes (which is done *before* waiting for the grace period),
   the process could go away, and a new one with the same PID starts up. This is a pretty
   small time window, though, and acceptable to live with in practice.

2. **Only works in PowerShell shells.** If, from a PowerShell shell with ConsoleBouncer
   loaded, you start a non-PowerShell child shell (such as `cmd.exe`), and then while using
   that child shell, you type ctrl+c, the leaf-most PowerShell shell will kill your
   current shell. To work around this problem, you can temporarily disable ConsoleBouncer
   by running "`Set-ConsoleBouncerOption -Disarm`".

3. **Relies on getting a ctrl+c signal.** Some child processes may disable ctrl+c handling
   (via `SetConsoleMode`) (they may then "manually" handle ctrl+c by handling ctrl-keydown
   followed by c-keydown). If this happens, there is nothing ConsoleBouncer can do--it is
   up to the app that has disabled ctrl+c handling to honor the user's desire to cancel or
   stop or whatever it is the app decides ctrl+c means. Note that in some cases observed
   in the wild, only a few random processes in a large tree of build processes do this,
   and simply mashing on ctrl+c is enough to eventually get a signal to ConsoleBouncer.

