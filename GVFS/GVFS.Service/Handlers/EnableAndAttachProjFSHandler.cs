using GVFS.Common;
using GVFS.Common.FileSystem;
using GVFS.Common.NamedPipes;
using GVFS.Common.Tracing;
using GVFS.Platform.Windows;

namespace GVFS.Service.Handlers
{
    public class EnableAndAttachProjFSHandler : MessageHandler
    {
        private const string EtwArea = nameof(EnableAndAttachProjFSHandler);

        private static object enablePrjFltLock = new object();

        private NamedPipeServer.Connection connection;
        private NamedPipeMessages.EnableAndAttachProjFSRequest request;
        private ITracer tracer;

        public EnableAndAttachProjFSHandler(
            ITracer tracer,
            NamedPipeServer.Connection connection,
            NamedPipeMessages.EnableAndAttachProjFSRequest request)
        {
            this.tracer = tracer;
            this.connection = connection;
            this.request = request;
        }

        public static bool TryEnablePrjFlt(ITracer tracer, out string error)
        {
            error = null;
            EventMetadata prjFltHealthMetadata = new EventMetadata();
            prjFltHealthMetadata.Add("Area", EtwArea);

            PhysicalFileSystem fileSystem = new PhysicalFileSystem();

            lock (enablePrjFltLock)
            {
                bool isPrjfltServiceInstalled;
                bool isPrjfltDriverInstalled;
                bool isNativeProjFSLibInstalled;
                bool isPrjfltServiceRunning = ProjFSFilter.IsServiceRunningAndInstalled(tracer, fileSystem, out isPrjfltServiceInstalled, out isPrjfltDriverInstalled, out isNativeProjFSLibInstalled);

                prjFltHealthMetadata.Add($"Initial_{nameof(isPrjfltDriverInstalled)}", isPrjfltDriverInstalled);
                prjFltHealthMetadata.Add($"Initial_{nameof(isPrjfltServiceInstalled)}", isPrjfltServiceInstalled);
                prjFltHealthMetadata.Add($"Initial_{nameof(isPrjfltServiceRunning)}", isPrjfltServiceRunning);
                prjFltHealthMetadata.Add($"Initial_{nameof(isNativeProjFSLibInstalled)}", isNativeProjFSLibInstalled);

                if (!isPrjfltServiceRunning)
                {
                    if (!isPrjfltServiceInstalled || !isPrjfltDriverInstalled)
                    {
                        bool isProjFSFeatureAvailable;
                        if (ProjFSFilter.TryEnableOptionalFeature(tracer, fileSystem, out isProjFSFeatureAvailable))
                        {
                            isPrjfltServiceInstalled = true;
                            isPrjfltDriverInstalled = true;
                        }
                        else
                        {
                            error = "Failed to enable PrjFlt optional feature";
                            tracer.RelatedError($"{nameof(TryEnablePrjFlt)}: {error}");
                        }

                        prjFltHealthMetadata.Add(nameof(isProjFSFeatureAvailable), isProjFSFeatureAvailable);
                    }

                    if (isPrjfltServiceInstalled)
                    {
                        if (ProjFSFilter.TryStartService(tracer))
                        {
                            isPrjfltServiceRunning = true;
                        }
                        else
                        {
                            error = "Failed to start prjflt service";
                            tracer.RelatedError($"{nameof(TryEnablePrjFlt)}: {error}");
                        }
                    }
                }

                isNativeProjFSLibInstalled = ProjFSFilter.IsNativeLibInstalled(tracer, fileSystem);
                if (!isNativeProjFSLibInstalled)
                {
                    error = "Native ProjFS library is not installed. Ensure the Windows 'Client-ProjFS' optional feature is enabled.";
                    tracer.RelatedError($"{nameof(TryEnablePrjFlt)}: {error}");
                }

                bool isAutoLoggerEnabled = ProjFSFilter.IsAutoLoggerEnabled(tracer);
                prjFltHealthMetadata.Add($"Initial_{nameof(isAutoLoggerEnabled)}", isAutoLoggerEnabled);

                if (!isAutoLoggerEnabled)
                {
                    if (ProjFSFilter.TryEnableAutoLogger(tracer))
                    {
                        isAutoLoggerEnabled = true;
                    }
                    else
                    {
                        tracer.RelatedError($"{nameof(TryEnablePrjFlt)}: Failed to enable prjflt AutoLogger");
                    }
                }

                prjFltHealthMetadata.Add(nameof(isPrjfltDriverInstalled), isPrjfltDriverInstalled);
                prjFltHealthMetadata.Add(nameof(isPrjfltServiceInstalled), isPrjfltServiceInstalled);
                prjFltHealthMetadata.Add(nameof(isPrjfltServiceRunning), isPrjfltServiceRunning);
                prjFltHealthMetadata.Add(nameof(isNativeProjFSLibInstalled), isNativeProjFSLibInstalled);
                prjFltHealthMetadata.Add(nameof(isAutoLoggerEnabled), isAutoLoggerEnabled);
                tracer.RelatedEvent(EventLevel.Informational, $"{nameof(TryEnablePrjFlt)}_Summary", prjFltHealthMetadata, Keywords.Telemetry);

                return isPrjfltDriverInstalled && isPrjfltServiceInstalled && isPrjfltServiceRunning && isNativeProjFSLibInstalled;
            }
        }

        public void Run()
        {
            string errorMessage;
            NamedPipeMessages.CompletionState state = NamedPipeMessages.CompletionState.Success;

            if (!TryEnablePrjFlt(this.tracer, out errorMessage))
            {
                state = NamedPipeMessages.CompletionState.Failure;
                this.tracer.RelatedError("Unable to install or enable PrjFlt. Enlistment root: {0} \nError: {1} ", this.request.EnlistmentRoot, errorMessage);
            }

            if (!string.IsNullOrEmpty(this.request.EnlistmentRoot))
            {
                if (!ProjFSFilter.TryAttach(this.request.EnlistmentRoot, out errorMessage))
                {
                    state = NamedPipeMessages.CompletionState.Failure;
                    this.tracer.RelatedError("Unable to attach filter to volume. Enlistment root: {0} \nError: {1} ", this.request.EnlistmentRoot, errorMessage);
                }
            }

            NamedPipeMessages.EnableAndAttachProjFSRequest.Response response = new NamedPipeMessages.EnableAndAttachProjFSRequest.Response();

            response.State = state;
            response.ErrorMessage = errorMessage;

            this.WriteToClient(response.ToMessage(), this.connection, this.tracer);
        }
    }
}
