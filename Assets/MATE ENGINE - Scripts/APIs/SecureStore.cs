using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

// Encrypts small secrets (e.g. API keys) for local storage.
// Windows: DPAPI (CurrentUser scope) - the blob can only be decrypted by the same Windows user.
// Other platforms: AES with a key derived from the device id.
public static class SecureStore
{
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MateEngine.SecureStore.v1");

    public static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        try
        {
            byte[] data = Encoding.UTF8.GetBytes(plain);
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            byte[] enc = DpapiProtect(data);
#else
            byte[] enc = AesProtect(data);
#endif
            return enc == null ? "" : Convert.ToBase64String(enc);
        }
        catch (Exception e)
        {
            Debug.LogError("[SecureStore] Protect failed: " + e.Message);
            return "";
        }
    }

    public static string Unprotect(string base64)
    {
        if (string.IsNullOrEmpty(base64)) return "";
        try
        {
            byte[] enc = Convert.FromBase64String(base64);
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            byte[] data = DpapiUnprotect(enc);
#else
            byte[] data = AesUnprotect(enc);
#endif
            return data == null ? "" : Encoding.UTF8.GetString(data);
        }
        catch (Exception e)
        {
            Debug.LogWarning("[SecureStore] Unprotect failed: " + e.Message);
            return "";
        }
    }

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string szDataDescr, ref DATA_BLOB pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, IntPtr ppszDataDescr, ref DATA_BLOB pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

    [DllImport("kernel32.dll")]
    static extern IntPtr LocalFree(IntPtr hMem);

    static byte[] DpapiProtect(byte[] data) => Dpapi(data, true);
    static byte[] DpapiUnprotect(byte[] data) => Dpapi(data, false);

    static byte[] Dpapi(byte[] input, bool protect)
    {
        var inBlob = new DATA_BLOB();
        var entBlob = new DATA_BLOB();
        var outBlob = new DATA_BLOB();
        try
        {
            inBlob = ToBlob(input);
            entBlob = ToBlob(Entropy);
            bool ok = protect
                ? CryptProtectData(ref inBlob, "MateEngine", ref entBlob, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref outBlob)
                : CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entBlob, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref outBlob);
            if (!ok) throw new Exception("DPAPI error " + Marshal.GetLastWin32Error());

            var result = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
            return result;
        }
        finally
        {
            if (inBlob.pbData != IntPtr.Zero) Marshal.FreeHGlobal(inBlob.pbData);
            if (entBlob.pbData != IntPtr.Zero) Marshal.FreeHGlobal(entBlob.pbData);
            if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
        }
    }

    static DATA_BLOB ToBlob(byte[] data)
    {
        var blob = new DATA_BLOB { cbData = data.Length, pbData = Marshal.AllocHGlobal(data.Length) };
        Marshal.Copy(data, 0, blob.pbData, data.Length);
        return blob;
    }
#else
    static byte[] DeriveKey()
    {
        using (var kdf = new Rfc2898DeriveBytes(SystemInfo.deviceUniqueIdentifier, Entropy, 10000))
            return kdf.GetBytes(32);
    }

    static byte[] AesProtect(byte[] data)
    {
        using (var aes = Aes.Create())
        {
            aes.Key = DeriveKey();
            aes.GenerateIV();
            using (var enc = aes.CreateEncryptor())
            {
                byte[] cipher = enc.TransformFinalBlock(data, 0, data.Length);
                byte[] result = new byte[aes.IV.Length + cipher.Length];
                Buffer.BlockCopy(aes.IV, 0, result, 0, aes.IV.Length);
                Buffer.BlockCopy(cipher, 0, result, aes.IV.Length, cipher.Length);
                return result;
            }
        }
    }

    static byte[] AesUnprotect(byte[] blob)
    {
        using (var aes = Aes.Create())
        {
            aes.Key = DeriveKey();
            byte[] iv = new byte[16];
            Buffer.BlockCopy(blob, 0, iv, 0, 16);
            aes.IV = iv;
            using (var dec = aes.CreateDecryptor())
                return dec.TransformFinalBlock(blob, 16, blob.Length - 16);
        }
    }
#endif
}
