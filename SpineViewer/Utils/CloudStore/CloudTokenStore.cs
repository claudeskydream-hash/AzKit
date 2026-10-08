using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace SpineViewer.Utils
{
    /// <summary>
    /// [AzureSail 新增] 把星火后台 token <b>加密</b>存在本机，下次打开 SpineViewer 不用再粘贴。
    ///
    /// 用 Windows DPAPI（<c>CryptProtectData</c>，当前用户范围）加密：只有同一台机器上的同一个 Windows 账号解得开，
    /// 文件被拷走也没用。直接调 crypt32.dll，不额外引入 NuGet 包。
    /// 存的是解析出来的 token 本身（不是整段 Cookie）。
    /// </summary>
    public static class CloudTokenStore
    {
        /// <summary>保存位置</summary>
        public static string FilePath => Path.Combine(App.DataDirectory, "cloudtoken.bin");

        /// <summary>加密后保存</summary>
        public static void Save(string token)
        {
            Directory.CreateDirectory(App.DataDirectory);
            File.WriteAllBytes(FilePath, Protect(Encoding.UTF8.GetBytes(token)));
        }

        /// <summary>读出并解密；没存过、或解不开（换了机器 / 账号、文件损坏）返回 null</summary>
        public static string? Load()
        {
            if (!File.Exists(FilePath))
                return null;

            byte[]? plain = Unprotect(File.ReadAllBytes(FilePath));
            return plain is null ? null : Encoding.UTF8.GetString(plain);
        }

        /// <summary>删掉已存的 token</summary>
        public static void Delete()
        {
            if (File.Exists(FilePath))
                File.Delete(FilePath);
        }

        // =============== DPAPI ===============

        [StructLayout(LayoutKind.Sequential)]
        private struct DataBlob
        {
            public int Size;
            public IntPtr Data;
        }

        /// <summary>不弹任何界面</summary>
        private const int CryptProtectUiForbidden = 0x1;

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptProtectData(ref DataBlob dataIn, string? description, IntPtr entropy,
            IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, IntPtr entropy,
            IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr mem);

        private static byte[] Protect(byte[] plain) =>
            Transform(plain, (ref DataBlob input, ref DataBlob output) =>
                CryptProtectData(ref input, "AzKit CloudSave token", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, ref output))
            ?? throw new IOException("加密 token 失败（Win32 错误 " + Marshal.GetLastWin32Error() + "）");

        private static byte[]? Unprotect(byte[] cipher) =>
            Transform(cipher, (ref DataBlob input, ref DataBlob output) =>
                CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, ref output));

        private delegate bool BlobCall(ref DataBlob input, ref DataBlob output);

        /// <summary>把字节放进非托管内存交给 DPAPI，再把结果拷回来并释放。失败返回 null</summary>
        private static byte[]? Transform(byte[] data, BlobCall call)
        {
            GCHandle pin = GCHandle.Alloc(data, GCHandleType.Pinned);
            DataBlob input = new() { Size = data.Length, Data = pin.AddrOfPinnedObject() };
            DataBlob output = new();
            try
            {
                if (!call(ref input, ref output))
                    return null;

                byte[] result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, output.Size);
                return result;
            }
            finally
            {
                pin.Free();
                if (output.Data != IntPtr.Zero)
                    LocalFree(output.Data);
            }
        }
    }
}
