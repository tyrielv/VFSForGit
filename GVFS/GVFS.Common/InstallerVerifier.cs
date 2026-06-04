using GVFS.Common.Tracing;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace GVFS.Common
{
    /// <summary>
    /// Verifies that an installer executable is a genuine VFS for Git installer
    /// by checking its Authenticode signature and PE version info.
    /// </summary>
    public static class InstallerVerifier
    {
        public const string ExpectedProductName = "VFS for Git";
        public const string ExpectedSignerCommonName = "Microsoft Corporation";

        /// <summary>
        /// Verifies the installer at the given path. Returns true if the
        /// installer passes all checks, false otherwise.
        /// </summary>
        /// <param name="allowUnsigned">
        /// When true, skip Authenticode verification (for dev/test builds).
        /// Product identity is still checked.
        /// </param>
        public static bool TryVerifyInstaller(
            ITracer tracer,
            string installerPath,
            bool allowUnsigned,
            out string error)
        {
            ArgumentNullException.ThrowIfNull(tracer);
            ArgumentNullException.ThrowIfNull(installerPath);

            if (!File.Exists(installerPath))
            {
                error = $"Installer not found: {installerPath}";
                return false;
            }

            // Always verify product identity, even when unsigned is allowed.
            if (!TryVerifyProductIdentity(tracer, installerPath, out error))
            {
                return false;
            }

            if (allowUnsigned)
            {
                tracer.RelatedWarning(
                    $"{nameof(InstallerVerifier)}: Skipping Authenticode verification (--allow-unsigned)");
                error = null;
                return true;
            }

            if (!TryVerifyAuthenticodeSignature(tracer, installerPath, out error))
            {
                return false;
            }

            error = null;
            return true;
        }

        private static bool TryVerifyProductIdentity(
            ITracer tracer,
            string installerPath,
            out string error)
        {
            FileVersionInfo versionInfo;
            try
            {
                versionInfo = FileVersionInfo.GetVersionInfo(installerPath);
            }
            catch (Exception ex)
            {
                error = $"Failed to read version info from {installerPath}: {ex.Message}";
                tracer.RelatedError(error);
                return false;
            }

            string productName = versionInfo.ProductName?.Trim();
            if (!string.Equals(productName, ExpectedProductName, StringComparison.OrdinalIgnoreCase))
            {
                error = $"Installer ProductName '{productName}' does not match expected '{ExpectedProductName}'";
                tracer.RelatedError($"{nameof(InstallerVerifier)}: {error}");
                return false;
            }

            tracer.RelatedInfo(
                $"{nameof(InstallerVerifier)}: Product identity verified — " +
                $"ProductName='{productName}', FileVersion='{versionInfo.FileVersion?.Trim()}'");

            error = null;
            return true;
        }

        private static bool TryVerifyAuthenticodeSignature(
            ITracer tracer,
            string installerPath,
            out string error)
        {
            X509Certificate2 certificate;
            try
            {
                X509Certificate basicCert = X509Certificate.CreateFromSignedFile(installerPath);
                certificate = new X509Certificate2(basicCert);
            }
            catch (CryptographicException ex)
            {
                error = $"Installer is not signed or has an invalid signature: {ex.Message}";
                tracer.RelatedError($"{nameof(InstallerVerifier)}: {error}");
                return false;
            }

            using (certificate)
            {
                // Verify the certificate chain is valid.
                using (X509Chain chain = new X509Chain())
                {
                    chain.ChainPolicy.RevocationMode = X509RevocationMode.Online;
                    chain.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;

                    if (!chain.Build(certificate))
                    {
                        string chainErrors = string.Join("; ",
                            chain.ChainStatus.Select(s => $"{s.Status}: {s.StatusInformation}"));
                        error = $"Certificate chain validation failed: {chainErrors}";
                        tracer.RelatedError($"{nameof(InstallerVerifier)}: {error}");
                        return false;
                    }
                }

                // Verify the signer is Microsoft Corporation.
                string subject = certificate.Subject;
                if (!subject.Contains($"CN={ExpectedSignerCommonName}", StringComparison.OrdinalIgnoreCase))
                {
                    error = $"Installer signed by unexpected publisher: {subject}";
                    tracer.RelatedError($"{nameof(InstallerVerifier)}: {error}");
                    return false;
                }

                tracer.RelatedInfo(
                    $"{nameof(InstallerVerifier)}: Authenticode signature verified — " +
                    $"Subject='{subject}'");
            }

            error = null;
            return true;
        }
    }
}
