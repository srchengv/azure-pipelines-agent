﻿using Microsoft.VisualStudio.Services.Agent.Util;
using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Diagnostics;

namespace Microsoft.VisualStudio.Services.Agent.Worker.Handlers
{
    [ServiceLocator(Default = typeof(PowerShell3Handler))]
    public interface IPowerShell3Handler : IHandler
    {
        PowerShell3HandlerData Data { get; set; }
    }

    public sealed class PowerShell3Handler : Handler, IPowerShell3Handler
    {
        public PowerShell3HandlerData Data { get; set; }

        public async Task RunAsync()
        {
            // Validate args.
            Trace.Entering();
            ArgUtil.NotNull(Data, nameof(Data));
            ArgUtil.NotNull(ExecutionContext, nameof(ExecutionContext));
            ArgUtil.NotNull(Inputs, nameof(Inputs));
            ArgUtil.Directory(TaskDirectory, nameof(TaskDirectory));


            // Update the env dictionary.
            AddInputsToEnvironment();
            AddEndpointsToEnvironment();
            AddSecureFilesToEnvironment();
            AddVariablesToEnvironment();
            AddTaskVariablesToEnvironment();
            AddPrependPathToEnvironment();

            // Resolve the target script.
            ArgUtil.NotNullOrEmpty(Data.Target, nameof(Data.Target));
            string scriptFile = Path.Combine(TaskDirectory, Data.Target);
            ArgUtil.File(scriptFile, nameof(scriptFile));

            // Resolve the VSTS Task SDK module definition.
            string scriptDirectory = Path.GetDirectoryName(scriptFile);
            string moduleFile = Path.Combine(scriptDirectory, @"ps_modules", "VstsTaskSdk", "VstsTaskSdk.psd1");
            ArgUtil.File(moduleFile, nameof(moduleFile));

            // Craft the args to pass to PowerShell.exe.
            string powerShellExeArgs = StringUtil.Format(
                @"-NoLogo -Sta -NoProfile -NonInteractive -ExecutionPolicy Unrestricted -Command "". ([scriptblock]::Create('if (!$PSHOME) {{ $null = Get-Item -LiteralPath ''variable:PSHOME'' }} else {{ Import-Module -Name ([System.IO.Path]::Combine($PSHOME, ''Modules\Microsoft.PowerShell.Management\Microsoft.PowerShell.Management.psd1'')) ; Import-Module -Name ([System.IO.Path]::Combine($PSHOME, ''Modules\Microsoft.PowerShell.Utility\Microsoft.PowerShell.Utility.psd1'')) }}')) 2>&1 | ForEach-Object {{ Write-Verbose $_.Exception.Message -Verbose }} ; Import-Module -Name '{0}' -ArgumentList @{{ NonInteractive = $true }} -ErrorAction Stop ; $VerbosePreference = '{1}' ; $DebugPreference = '{1}' ; Invoke-VstsTaskScript -ScriptBlock ([scriptblock]::Create('. ''{2}'''))""",
                StepHost.ResolvePathForStepHost(moduleFile).Replace("'", "''"), // nested within a single-quoted string
                ExecutionContext.Variables.System_Debug == true ? "Continue" : "SilentlyContinue",
                StepHost.ResolvePathForStepHost(scriptFile).Replace("'", "''''")); // nested within a single-quoted string within a single-quoted string

            // Resolve powershell.exe.
            string powerShellExe = HostContext.GetService<IPowerShellExeUtil>().GetPath(); // The location of powershell.exe might be wrong when running inside container
            ArgUtil.NotNullOrEmpty(powerShellExe, nameof(powerShellExe));

            // Invoke the process.
            StepHost.OutputDataReceived += OnDataReceived;
            StepHost.ErrorDataReceived += OnDataReceived;
            bool persistChcp = false;
#if OS_WINDOWS
            if (ExecutionContext.Variables.Retain_Default_Encoding != true)
            {
                // Make sure code page is UTF8 so that special characters in Linuxy things are caught.
                using (var p = HostContext.CreateService<IProcessInvoker>())
                {
                    int exitCode = await p.ExecuteAsync(workingDirectory: HostContext.GetDirectory(WellKnownDirectory.Work),
                                             fileName: WhichUtil.Which("chcp", true, Trace),
                                             arguments: "65001",
                                             environment: null,
                                             requireExitCodeZero: false,
                                             outputEncoding: null,
                                             killProcessOnCancel: false,
                                             contentsToStandardIn: null,
                                             cancellationToken: ExecutionContext.CancellationToken,
                                             persistChcp: true);
                    if (exitCode == 0)
                    {
                        Trace.Info("Successfully changed to code page 65001 (UTF8)");
                        persistChcp = true;
                    }
                    else
                    {
                        Trace.Warning($"'chcp 65001' failed with exit code {exitCode}");
                    }
                }
            }
#endif
            // Execute the process. Exit code 0 should always be returned.
            // A non-zero exit code indicates infrastructural failure.
            // Task failure should be communicated over STDOUT using ## commands.
            await StepHost.ExecuteAsync(workingDirectory: StepHost.ResolvePathForStepHost(scriptDirectory),
                                        fileName: powerShellExe,
                                        arguments: powerShellExeArgs,
                                        environment: Environment,
                                        requireExitCodeZero: true,
                                        outputEncoding: null,
                                        killProcessOnCancel: false,
                                        cancellationToken: ExecutionContext.CancellationToken,
                                        persistChcp: persistChcp);

#if OS_WINDOWS
            if (ExecutionContext.Variables.Retain_Default_Encoding != true)
            {
                using (var p = HostContext.CreateService<IProcessInvoker>())
                {
                    // Return to default code page
                    int exitCode = await p.ExecuteAsync(workingDirectory: HostContext.GetDirectory(WellKnownDirectory.Work),
                                             fileName: WhichUtil.Which("chcp", true, Trace),
                                             arguments: "437",
                                             environment: null,
                                             requireExitCodeZero: false,
                                             outputEncoding: null,
                                             killProcessOnCancel: false,
                                             contentsToStandardIn: null,
                                             cancellationToken: ExecutionContext.CancellationToken,
                                             persistChcp: true);
                    if (exitCode == 0)
                    {
                        Trace.Info("Successfully returned to code page 437 (UTF8)");
                    }
                    else
                    {
                        Trace.Warning($"'chcp 437' failed with exit code {exitCode}");
                    }
                }
            }
#endif
        }

        private void OnDataReceived(object sender, ProcessDataReceivedEventArgs e)
        {
            // This does not need to be inside of a critical section.
            // The logging queues and command handlers are thread-safe.
            if (!CommandManager.TryProcessCommand(ExecutionContext, e.Data))
            {
                ExecutionContext.Output(e.Data);
            }
        }
    }
}
