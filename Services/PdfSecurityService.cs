using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using iText.Kernel.Pdf;

namespace XTPdfMergeApp.Services
{
    /// <summary>Quyền được PDF khai báo; không suy diễn từ việc app có đang mở được file hay không.</summary>
    internal sealed record PdfSecurityInfo(
        bool IsEncrypted,
        bool IsOwner,
        bool CanPrint,
        bool CanCopy,
        bool CanModify,
        bool CanAnnotate,
        bool CanFillForms,
        string? Error = null);

    /// <summary>Thiết lập bảo vệ chuẩn PDF. Owner password luôn cần khi bật bảo vệ để quyền không trở thành "khóa giả".</summary>
    internal sealed record PdfProtectionOptions(
        string? CurrentOwnerPassword,
        string UserPassword,
        string OwnerPassword,
        bool AllowPrint,
        bool AllowCopy,
        bool AllowModify,
        bool AllowAnnotate,
        bool RemoveProtection = false);

    internal static class PdfSecurityService
    {
        private static readonly ConcurrentDictionary<string, (long Length, DateTime Stamp, string? Password, PdfSecurityInfo Info)> Cache = new(StringComparer.OrdinalIgnoreCase);
        public static Task<PdfSecurityInfo> ReadAsync(string path) => Task.Run(() => ReadCached(path));

        internal static PdfSecurityInfo ReadCached(string path)
        {
            try
            {
                path = Path.GetFullPath(path);
                var file = new FileInfo(path);
                string? password = PdfThumbnailService.TryGetDocumentPassword(path);
                if (file.Exists && Cache.TryGetValue(path, out var cached) && cached.Length == file.Length && cached.Stamp == file.LastWriteTimeUtc && cached.Password == password)
                    return cached.Info;
                var info = Read(path);
                if (file.Exists && info.Error == null)
                {
                    if (Cache.Count > 512) Cache.Clear();
                    Cache[path] = (file.Length, file.LastWriteTimeUtc, password, info);
                }
                return info;
            }
            catch (Exception ex) { return new PdfSecurityInfo(false, false, false, false, false, false, false, ex.Message); }
        }

        internal static ReaderProperties ReaderPropertiesFor(string path)
        {
            var properties = new ReaderProperties();
            if (PdfThumbnailService.TryGetDocumentPassword(path) is { Length: > 0 } password)
                properties.SetPassword(Encoding.UTF8.GetBytes(password));
            return properties;
        }

        internal static PdfReader AuthorizedReaderFor(string path, PdfPermissionOperation operation)
        {
            PdfPermissionPolicy.EnsureAllowed(path, operation);
            // iText demands the owner password for all writes/copies. Lift that blanket check only
            // AFTER checking the PDF's declared permission for this particular operation.
            return new PdfReader(path, ReaderPropertiesFor(path)).SetUnethicalReading(true);
        }

        private static PdfSecurityInfo Read(string path)
        {
            try
            {
                var properties = new ReaderProperties();
                if (PdfThumbnailService.TryGetDocumentPassword(path) is { Length: > 0 } password)
                    properties.SetPassword(Encoding.UTF8.GetBytes(password));

                using var reader = new PdfReader(path, properties);
                using var document = new PdfDocument(reader); // PdfReader parses encryption only when a PdfDocument opens it.
                if (!reader.IsEncrypted())
                    return new PdfSecurityInfo(false, true, true, true, true, true, true);

                bool owner = reader.IsOpenedWithFullPermission();
                int permissions = reader.GetPermissions();
                bool Allows(int permission) => owner || (permissions & permission) == permission;
                return new PdfSecurityInfo(
                    true,
                    owner,
                    Allows(EncryptionConstants.ALLOW_PRINTING),
                    Allows(EncryptionConstants.ALLOW_COPY),
                    Allows(EncryptionConstants.ALLOW_MODIFY_CONTENTS),
                    Allows(EncryptionConstants.ALLOW_MODIFY_ANNOTATIONS),
                    Allows(EncryptionConstants.ALLOW_FILL_IN));
            }
            catch (Exception ex)
            {
                return new PdfSecurityInfo(false, false, false, false, false, false, false, ex.Message);
            }
        }

        /// <summary>
        /// Đổi hoặc gỡ encryption bằng một bản ghi lại đầy đủ ra tệp cùng thư mục rồi thay thế nguyên tử.
        /// Encryption không thể đổi bằng incremental update; file gốc chỉ bị thay khi PDF mới đóng thành công.
        /// </summary>
        public static void ApplyProtection(string path, PdfProtectionOptions options)
        {
            if (options.RemoveProtection && string.IsNullOrEmpty(options.CurrentOwnerPassword))
                throw new InvalidOperationException("Enter the current owner password to remove protection.");
            if (!options.RemoveProtection && string.IsNullOrEmpty(options.OwnerPassword))
                throw new InvalidOperationException("An owner password is required to protect a PDF.");

            string directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("The PDF has no folder.");
            string temp = Path.Combine(directory, Path.GetFileName(path) + ".xtprotect." + Guid.NewGuid().ToString("N") + ".tmp");
            string backup = temp + ".bak";
            try
            {
                var readerProperties = new ReaderProperties();
                if (!string.IsNullOrEmpty(options.CurrentOwnerPassword))
                    readerProperties.SetPassword(Encoding.UTF8.GetBytes(options.CurrentOwnerPassword));

                var writerProperties = new WriterProperties();
                if (!options.RemoveProtection)
                {
                    int permissions = 0;
                    if (options.AllowPrint) permissions |= EncryptionConstants.ALLOW_PRINTING;
                    if (options.AllowCopy) permissions |= EncryptionConstants.ALLOW_COPY;
                    if (options.AllowModify) permissions |= EncryptionConstants.ALLOW_MODIFY_CONTENTS;
                    if (options.AllowAnnotate) permissions |= EncryptionConstants.ALLOW_MODIFY_ANNOTATIONS;
                    writerProperties.SetStandardEncryption(
                        string.IsNullOrEmpty(options.UserPassword) ? null : Encoding.UTF8.GetBytes(options.UserPassword),
                        Encoding.UTF8.GetBytes(options.OwnerPassword),
                        permissions,
                        EncryptionConstants.ENCRYPTION_AES_256);
                }

                // Stamping không append: iText ghi lại toàn bộ PDF, cần thiết để thay /Encrypt.
                using (var reader = new PdfReader(path, readerProperties))
                using (var writer = new PdfWriter(temp, writerProperties))
                using (var document = new PdfDocument(reader, writer))
                {
                    // iText parses encryption while PdfDocument is constructed, not while PdfReader is constructed.
                    if (reader.IsEncrypted() && !reader.IsOpenedWithFullPermission())
                        throw new UnauthorizedAccessException("The current password is not an owner password, so PDF permissions cannot be changed.");
                }

                File.Replace(temp, path, backup, ignoreMetadataErrors: true);
                TryDelete(backup);
            }
            catch
            {
                TryDelete(temp);
                throw;
            }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { /* backup/temporary file is safer than hiding the original failure */ }
        }
    }
}
