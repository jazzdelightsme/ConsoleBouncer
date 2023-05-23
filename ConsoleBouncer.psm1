
try
{
    # Welcome to the future.
    [console]::OutputEncoding = [System.Text.Encoding]::UTF8

    $code = Get-Content -Raw "$PSScriptRoot\NativeMethods.cs"

    # We are going to change the name of the types so that we can load the code multiple
    # times in a single session, even if the code has changed. To avoid generating new
    # types unnecessarily, we'll use a hash of the source. The GetHashCode() hash is
    # probably not very good... but hopefully good enough for our purposes.
    $randomStr = $code.GetHashCode().ToString('x')
    $code = $code.Replace('namespace ConsoleBouncer', "namespace ConsoleBouncer_$randomStr")

    $script:NativeMethods = Add-Type -TypeDefinition $code -EA Stop -PassThru | Where-Object Name -eq 'NativeMethods'

    $script:TheBouncer = $NativeMethods::InstallBouncer()
}
finally { }

$MyInvocation.MyCommand.ScriptBlock.Module.OnRemove =
{
    try
    {
        if( $script:TheBouncer )
        {
            $script:TheBouncer.Dispose();
        }
    }
    finally { }
} # end OnRemove handler

<#
.SYNOPSIS
    Configures ConsoleBouncer options. These settings are shared between all shell
    processes that share a console window.

        -Verbosity              An integer (default 1); the larger it gets, the more
                                verbose messages make it to the console.

        -GracePeriodMillis      How long the ConsoleBouncer waits before terminating
                                loitering processes.

        -ClearProgress          Indicates that ConsoleBouncer should clear the taskbar
                                progress state after it terminates lingering processes.
                                The default is "true"; to turn it off, use
                                "-ClearProgress:$false".

        -IfNotAlreadySet        (Only affects the previous settings) Don't actually change
                                the setting if the setting has already been customized.
                                This should be used when setting options from your
                                $profile script, so that they don't wipe out manual
                                customizations performed by the user in a parent shell.

        -AllowedProcessName     Allows you to add process names to a special "allow list",
                                which will be allowed to stay when ctrl+c is pressed.
                                Currently defaults to { "kd", "cdb" }. You can also remove
                                names by prepending with "!". (Leave off the .exe
                                extension.)

        -Disable/-Enable        If disabled, the ConsoleBouncer's ctrl+c handler is still
                                called, but it does nothing (until reenabled).

        -Disarm/-Rearm          Only very subtly different from being disabled, "disarmed"
                                means that the ctrl+c handler runs as usual, including
                                masking ctrl+c signals for other handlers in non-leaf
                                processes, but it won't attempt to kill any processes. The
                                bouncer will stay disarmed until rearmed, or until the
                                shell that disarmed it exits.

    Run "Get-Help about_ConsoleBouncer" for more info about the ConsoleBouncer module.
#>
function Set-ConsoleBouncerOption
{
    [CmdletBinding( DefaultParameterSetName = 'DefaultParameterSet' )]
    param(
        [int] $Verbosity,

        [ValidateRange( 0, 60000 )]
        [int] $GracePeriodMillis,

        [switch] $ClearProgress,

        [switch] $IfNotAlreadySet,

        [string[]] $AllowedProcessName,

        [Parameter( Mandatory = $true, ParameterSetName = 'DisableParamSet' )]
        [switch] $Disable,

        [Parameter( Mandatory = $true, ParameterSetName = 'EnableParamSet' )]
        [switch] $Enable,

        [Parameter( Mandatory = $true, ParameterSetName = 'DisarmParamSet' )]
        [switch] $Disarm,

        [Parameter( Mandatory = $true, ParameterSetName = 'RearmParamSet' )]
        [switch] $Rearm
    )

    try
    {
        function SkipIt( $settingName )
        {
            [bool] $skip = $false

            # TODO: could "if not already set" be automagically detected by looking at our
            # stack and see if we are being called from a $profile script?
            if( $IfNotAlreadySet )
            {
                $skip = @( $script:TheBouncer.CustomizedSettings ) -contains $settingName

                if( $skip )
                {
                    Write-Verbose "Skipping setting $settingName, because it is already set, and -IfNotAlreadySet was specified."
                }
            }

            return $skip
        }

        function NoteThatSettingHasBeenCustomized( $settingName )
        {
            $set = @( $script:TheBouncer.CustomizedSettings )
            if( $set -notcontains $settingName )
            {
                $set += $settingName
                $script:TheBouncer.CustomizedSettings = $set
            }
        }

        if( $PSBoundParameters.ContainsKey( 'Verbosity' ) )
        {
            if( !(SkipIt 'Verbosity') )
            {
                $script:TheBouncer.Verbosity = $Verbosity
                NoteThatSettingHasBeenCustomized 'Verbosity'
            }
        }

        if( $PSBoundParameters.ContainsKey( 'AllowedProcessName' ) )
        {
            # We don't pay attention to -IfNotAlreadySet for these, because the parameter
            # is additive/subtractive anyway.

            $allNames = @( $script:TheBouncer.AllowedProcesses )

            foreach( $name in $AllowedProcessName )
            {
                if( $name.StartsWith( '!' ) )
                {
                    $allNames = @( $allNames | Where-Object { $_ -ne $name.Substring( 1 ) } )
                }
                else
                {
                    $allNames += $name
                }
            }

            $script:TheBouncer.AllowedProcesses = $allNames
        }

        if( $PSBoundParameters.ContainsKey( 'GracePeriodMillis' ) )
        {
            if( !(SkipIt 'GracePeriodMillis') )
            {
                $script:TheBouncer.GracePeriodMillis = $GracePeriodMillis
                NoteThatSettingHasBeenCustomized 'GracePeriodMillis'
            }
        }

        if( $PSBoundParameters.ContainsKey( 'ClearProgress' ) )
        {
            if( !(SkipIt 'ClearProgress') )
            {
                $script:TheBouncer.ClearProgress = $ClearProgress
                NoteThatSettingHasBeenCustomized 'ClearProgress'
            }
        }

        if( $PSBoundParameters.ContainsKey( 'Disable' ) )
        {
            $script:TheBouncer.Disabled = $Disable
        }
        elseif( $PSBoundParameters.ContainsKey( 'Enable' ) )
        {
            $script:TheBouncer.Disabled = !$Enable
        }
        elseif( $PSBoundParameters.ContainsKey( 'Disarm' ) )
        {
            if( $Disarm )
            {
                $script:TheBouncer.DisarmedBy = $pid
            }
            else
            {
                # Should we really allow this usage?
                $script:TheBouncer.DisarmedBy = 0
            }
        }
        elseif( $PSBoundParameters.ContainsKey( 'Rearm' ) )
        {
            if( $Rearm )
            {
                $script:TheBouncer.DisarmedBy = 0
            }
        }
    }
    catch
    {
        Write-Error $_
    }
}

<#
.SYNOPSIS
    Gets ConsoleBouncer options. These settings are shared between all shell
    processes that share a console window. Run "Set-ConsoleBouncerOption -?" for more info
    on the data returned.

    Run "Get-Help about_ConsoleBouncer" for more info about the ConsoleBouncer module.
#>
function Get-ConsoleBouncerOption
{
    [CmdletBinding()]
    param()

    try
    {
        $options = @{
            Verbosity = $script:TheBouncer.Verbosity
            AllowedProcessName = $script:TheBouncer.AllowedProcesses
            GracePeriodMillis = $script:TheBouncer.GracePeriodMillis
            Disabled = $script:TheBouncer.Disabled
            DisarmedBy = $script:TheBouncer.DisarmedBy
            ClearProgress = $script:TheBouncer.ClearProgress
        }

        return [PSCustomObject] $options
    }
    catch
    {
        Write-Error $_
    }
}

# Private function for debugging.
#
# Use it like this: "& (gmo ConsoleBouncer) { DumpAliases }"
function DumpAliases
{
    [CmdletBinding()]
    param()

    $NativeMethods::GetConsoleAliases( "(* ^_^) ConsoleBouncer" ) | Format-Table @(
        @{ Label = 'Name' ; Expression = { $_.Item1 } }
        @{ Label = 'Value' ; Expression = { $_.Item2 } }
    )
}

