using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace AnomalyStudio.Core.Storage;

/// <summary>
/// 秘密の値（AI アシストの API キー）を OS の資格情報ストアに保存する。暗号化とファイルの管理は OS が行い、
/// ログイン中のユーザーだけが読める（この PC だけに保存し、ローミングしない）。
/// <list type="bullet">
/// <item>Windows: 資格情報マネージャー（汎用資格情報）</item>
/// <item>macOS: ログインキーチェーン（汎用パスワード。サービス名 = <c>target</c>）。署名していないアプリは、更新で署名が変わるたびに
/// キーチェーンへのアクセスの許可を求められることがある</item>
/// </list>
/// それ以外の OS では保存しない（読み込みは null）。
/// </summary>
public static class CredentialStore
{
    /// <summary>保存先の名前（画面の説明に使う）。</summary>
    public static string StoreName =>
        OperatingSystem.IsMacOS() ? "キーチェーン" : "Windows の資格情報マネージャー";

    /// <summary>保存した値。なければ null。</summary>
    public static string? Read(string target) =>
        OperatingSystem.IsWindows() ? WindowsCredentials.Read(target)
        : OperatingSystem.IsMacOS() ? MacKeychain.Read(target)
        : null;

    /// <summary>保存する（同じ名前は置き換え）。</summary>
    public static void Write(string target, string userName, string secret, string comment)
    {
        if (OperatingSystem.IsWindows())
        {
            WindowsCredentials.Write(target, userName, secret, comment);
        }
        else if (OperatingSystem.IsMacOS())
        {
            MacKeychain.Write(target, userName, secret);
        }
    }

    /// <summary>削除する。なければ false。</summary>
    public static bool Delete(string target) =>
        OperatingSystem.IsWindows() ? WindowsCredentials.Delete(target)
        : OperatingSystem.IsMacOS() && MacKeychain.Delete(target);

    /// <summary>Windows の資格情報マネージャー（汎用資格情報）。</summary>
    private static class WindowsCredentials
    {
        private const uint GenericType = 1;
        private const uint PersistLocalMachine = 2;
        private const int NotFound = 1168;

        public static string? Read(string target)
        {
            if (!CredRead(target, GenericType, 0, out var pointer))
            {
                var error = Marshal.GetLastWin32Error();
                return error == NotFound ? null : throw new Win32Exception(error);
            }

            try
            {
                var credential = Marshal.PtrToStructure<Credential>(pointer);
                if (credential.CredentialBlobSize == 0)
                {
                    return string.Empty;
                }

                var bytes = new byte[credential.CredentialBlobSize];
                Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
                return Encoding.Unicode.GetString(bytes);
            }
            finally
            {
                CredFree(pointer);
            }
        }

        public static void Write(string target, string userName, string secret, string comment)
        {
            var bytes = Encoding.Unicode.GetBytes(secret);
            var blob = Marshal.AllocHGlobal(bytes.Length);
            try
            {
                Marshal.Copy(bytes, 0, blob, bytes.Length);
                var credential = new Credential
                {
                    Type = GenericType,
                    TargetName = target,
                    Comment = comment,
                    CredentialBlobSize = (uint)bytes.Length,
                    CredentialBlob = blob,
                    Persist = PersistLocalMachine,
                    UserName = userName,
                };
                if (!CredWrite(ref credential, 0))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
            }
            finally
            {
                // 秘密の値をメモリに残さない
                Marshal.Copy(new byte[bytes.Length], 0, blob, bytes.Length);
                Array.Clear(bytes);
                Marshal.FreeHGlobal(blob);
            }
        }

        public static bool Delete(string target)
        {
            if (CredDelete(target, GenericType, 0))
            {
                return true;
            }

            var error = Marshal.GetLastWin32Error();
            return error == NotFound ? false : throw new Win32Exception(error);
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct Credential
        {
            public uint Flags;
            public uint Type;
            public string TargetName;
            public string? Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            public string? TargetAlias;
            public string? UserName;
        }

        [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

        [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredWrite(ref Credential credential, uint flags);

        [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredDelete(string target, uint type, uint flags);

        [DllImport("advapi32.dll")]
        private static extern void CredFree(IntPtr buffer);
    }

    /// <summary>
    /// macOS のログインキーチェーン（Security.framework の汎用パスワード）。サービス名で探し、アカウント名は問わない。
    /// 値を引数に渡してコマンド（security）を起動すると他のプロセスから見えるので、フレームワークを直接呼ぶ。
    /// </summary>
    private static class MacKeychain
    {
        private const string Security = "/System/Library/Frameworks/Security.framework/Security";
        private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        private const int Success = 0;
        private const int ItemNotFound = -25300;

        public static string? Read(string target)
        {
            var service = Encoding.UTF8.GetBytes(target);
            var status = SecKeychainFindGenericPassword(
                IntPtr.Zero, (uint)service.Length, service, 0, null, out var length, out var data, out var item);
            if (status == ItemNotFound)
            {
                return null;
            }

            Check(status);
            try
            {
                if (length == 0)
                {
                    return string.Empty;
                }

                var bytes = new byte[length];
                Marshal.Copy(data, bytes, 0, bytes.Length);
                var value = Encoding.UTF8.GetString(bytes);
                Array.Clear(bytes);
                return value;
            }
            finally
            {
                SecKeychainItemFreeContent(IntPtr.Zero, data);
                Release(item);
            }
        }

        public static void Write(string target, string userName, string secret)
        {
            var service = Encoding.UTF8.GetBytes(target);
            var password = Encoding.UTF8.GetBytes(secret);
            try
            {
                var status = SecKeychainFindGenericPassword(
                    IntPtr.Zero, (uint)service.Length, service, 0, null, out _, out _, out var existing);
                if (status == Success)
                {
                    try
                    {
                        Check(SecKeychainItemModifyAttributesAndData(existing, IntPtr.Zero, (uint)password.Length, password));
                    }
                    finally
                    {
                        Release(existing);
                    }

                    return;
                }

                if (status != ItemNotFound)
                {
                    Check(status);
                }

                var account = Encoding.UTF8.GetBytes(userName);
                Check(SecKeychainAddGenericPassword(
                    IntPtr.Zero, (uint)service.Length, service, (uint)account.Length, account, (uint)password.Length, password, out var added));
                Release(added);
            }
            finally
            {
                Array.Clear(password);
            }
        }

        public static bool Delete(string target)
        {
            var service = Encoding.UTF8.GetBytes(target);
            var status = SecKeychainFindGenericPassword(
                IntPtr.Zero, (uint)service.Length, service, 0, null, out _, out _, out var item);
            if (status == ItemNotFound)
            {
                return false;
            }

            Check(status);
            try
            {
                Check(SecKeychainItemDelete(item));
                return true;
            }
            finally
            {
                Release(item);
            }
        }

        private static void Check(int status)
        {
            if (status != Success)
            {
                throw new InvalidOperationException($"キーチェーンの操作に失敗しました（OSStatus {status}）。");
            }
        }

        private static void Release(IntPtr item)
        {
            if (item != IntPtr.Zero)
            {
                CFRelease(item);
            }
        }

        [DllImport(Security)]
        private static extern int SecKeychainFindGenericPassword(
            IntPtr keychainOrArray, uint serviceNameLength, byte[] serviceName, uint accountNameLength, byte[]? accountName,
            out uint passwordLength, out IntPtr passwordData, out IntPtr itemRef);

        [DllImport(Security)]
        private static extern int SecKeychainAddGenericPassword(
            IntPtr keychain, uint serviceNameLength, byte[] serviceName, uint accountNameLength, byte[] accountName,
            uint passwordLength, byte[] passwordData, out IntPtr itemRef);

        [DllImport(Security)]
        private static extern int SecKeychainItemModifyAttributesAndData(IntPtr itemRef, IntPtr attrList, uint length, byte[] data);

        [DllImport(Security)]
        private static extern int SecKeychainItemDelete(IntPtr itemRef);

        [DllImport(Security)]
        private static extern int SecKeychainItemFreeContent(IntPtr attrList, IntPtr data);

        [DllImport(CoreFoundation)]
        private static extern void CFRelease(IntPtr cf);
    }
}
