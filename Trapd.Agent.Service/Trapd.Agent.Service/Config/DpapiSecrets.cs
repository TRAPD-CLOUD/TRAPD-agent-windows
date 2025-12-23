using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Trapd.Agent.Service.Config;

/// <summary>
/// Handles reading and decrypting secrets using Windows DPAPI.
/// Secrets are encrypted with DataProtectionScope.LocalMachine for service compatibility.
/// </summary>
public static class DpapiSecrets
{
    /// <summary>
    /// Reads and decrypts the API key from the secrets directory.
    /// The file is expected to be encrypted using ProtectedData.Protect with LocalMachine scope.
    /// </summary>
    /// <param name="dataDir">The data directory path.</param>
    /// <returns>The decrypted API key as a UTF-8 string.</returns>
    /// <exception cref="FileNotFoundException">Thrown when the API key file does not exist.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the decrypted API key is empty.</exception>
    /// <exception cref="CryptographicException">Thrown when decryption fails.</exception>
    public static string ReadApiKey(string dataDir)
    {
        ArgumentNullException.ThrowIfNull(dataDir);

        var apiKeyPath = DataDir.GetApiKeyPath(dataDir);

        if (!File.Exists(apiKeyPath))
        {
            throw new FileNotFoundException(
                $"API key file not found at '{apiKeyPath}'. " +
                "Please create the encrypted API key using: " +
                "TRAPD.Agent.Cli encrypt-api-key <your-api-key>",
                apiKeyPath);
        }

        // Read the encrypted bytes
        var encryptedBytes = File.ReadAllBytes(apiKeyPath);

        if (encryptedBytes.Length == 0)
        {
            throw new InvalidOperationException(
                $"API key file at '{apiKeyPath}' is empty.");
        }

        // Decrypt using DPAPI LocalMachine scope
        // This allows any process running on this machine to decrypt (suitable for Windows Services)
        byte[] decryptedBytes;
        try
        {
            decryptedBytes = ProtectedData.Unprotect(
                encryptedBytes,
                optionalEntropy: null,
                scope: DataProtectionScope.LocalMachine);
        }
        catch (CryptographicException ex)
        {
            throw new CryptographicException(
                $"Failed to decrypt API key from '{apiKeyPath}'. " +
                "The key may have been encrypted on a different machine or with different scope. " +
                $"Original error: {ex.Message}",
                ex);
        }

        var apiKey = Encoding.UTF8.GetString(decryptedBytes);

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                $"Decrypted API key from '{apiKeyPath}' is empty.");
        }

        return apiKey;
    }

    /// <summary>
    /// Encrypts and saves an API key to the secrets directory.
    /// Uses DPAPI with LocalMachine scope for service compatibility.
    /// </summary>
    /// <param name="dataDir">The data directory path.</param>
    /// <param name="apiKey">The API key to encrypt and save.</param>
    /// <exception cref="ArgumentException">Thrown when apiKey is null or empty.</exception>
    public static void WriteApiKey(string dataDir, string apiKey)
    {
        ArgumentNullException.ThrowIfNull(dataDir);
        
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ArgumentException("API key cannot be null or empty.", nameof(apiKey));
        }

        // Ensure secrets directory exists
        DataDir.EnsureDirectories(dataDir);

        var apiKeyPath = DataDir.GetApiKeyPath(dataDir);
        var plainBytes = Encoding.UTF8.GetBytes(apiKey);

        // Encrypt using DPAPI LocalMachine scope
        var encryptedBytes = ProtectedData.Protect(
            plainBytes,
            optionalEntropy: null,
            scope: DataProtectionScope.LocalMachine);

        File.WriteAllBytes(apiKeyPath, encryptedBytes);
    }

    /// <summary>
    /// Checks if the API key file exists.
    /// </summary>
    /// <param name="dataDir">The data directory path.</param>
    /// <returns>True if the API key file exists, false otherwise.</returns>
    public static bool ApiKeyExists(string dataDir)
    {
        ArgumentNullException.ThrowIfNull(dataDir);
        return File.Exists(DataDir.GetApiKeyPath(dataDir));
    }
}
