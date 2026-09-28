using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace XTPdfMergeApp.Services
{
    /// <summary>1 mẫu dấu (stamp): chữ có viền màu, hoặc ảnh (chữ ký / con dấu đã quét).</summary>
    public sealed record StampDefinition(string Kind, string Text, string Sub, string Color, string ImagePath)
    {
        public const string TextKind = "text", ImageKind = "image";
        public bool IsImage => Kind == ImageKind;

        private const char Sep = '\u001f';

        /// <summary>Ghi vào <see cref="QuickAnnotationSpec.Text"/> để đưa qua lệnh annotation (có Undo): loại, chữ, dòng phụ, màu, độ đục %, đường dẫn ảnh.</summary>
        public string Encode(int opacityPercent, string sub)
            => string.Join(Sep, Kind, Text, sub, Color, opacityPercent.ToString(), ImagePath);

        public static (StampDefinition Definition, int Opacity) Decode(string encoded)
        {
            var f = encoded.Split(Sep);
            string At(int i) => i < f.Length ? f[i] : "";
            int opacity = int.TryParse(At(4), out int o) ? Math.Clamp(o, 10, 100) : 100;
            return (new StampDefinition(At(0) is ImageKind ? ImageKind : TextKind, At(1), At(2), At(3).Length > 0 ? At(3) : "#C0392B", At(5)), opacity);
        }
    }

    /// <summary>Thư viện dấu: mẫu có sẵn + dấu của người dùng (%LocalAppData%\XTPdfReader\stamps.json, ảnh trong thư mục stamps).</summary>
    public static class StampLibrary
    {
        private static readonly JsonListStore<StampDefinition> _store = new("stamps.json");

        public static IReadOnlyList<StampDefinition> Standard { get; } = new[]
        {
            new StampDefinition(StampDefinition.TextKind, "APPROVED", "", "#0F8B6D", ""),
            new StampDefinition(StampDefinition.TextKind, "REVIEWED", "", "#2563EB", ""),
            new StampDefinition(StampDefinition.TextKind, "REJECTED", "", "#C0392B", ""),
            new StampDefinition(StampDefinition.TextKind, "FOR INFO", "", "#6B7280", ""),
            new StampDefinition(StampDefinition.TextKind, "DRAFT", "", "#D9640A", ""),
            new StampDefinition(StampDefinition.TextKind, "CONFIDENTIAL", "", "#7C3AED", ""),
        };

        public static IReadOnlyList<StampDefinition> Mine => _store.Items;

        public static void Add(StampDefinition definition)
        {
            _store.Items.Add(definition);
            _store.Save();
        }

        public static void Remove(StampDefinition definition)
        {
            if (_store.Items.Remove(definition)) _store.Save();
        }

        /// <summary>Chép ảnh vào thư mục dấu của app (để dấu vẫn dùng được khi ảnh gốc bị xoá/di chuyển) và trả đường dẫn bản chép.</summary>
        public static string ImportImage(string sourcePath)
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XTPdfReader", "stamps");
            Directory.CreateDirectory(dir);
            string target = Path.Combine(dir, Guid.NewGuid().ToString("N") + Path.GetExtension(sourcePath));
            File.Copy(sourcePath, target);
            return target;
        }
    }
}
