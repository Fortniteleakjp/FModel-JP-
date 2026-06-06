using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Controls;
using FModel.Framework;
using Serilog;

namespace FModel.Views.ReleaseNotes
{
    public partial class ReleaseNotesView : UserControl
    {
        public ReleaseNotesView() : this(string.Empty) { }

        public ReleaseNotesView(string version)
        {
            InitializeComponent();
            LoadMarkdown(version);
        }

        private void LoadMarkdown(string version)
        {
            var md = ReadEmbedded("ReleaseNotes.md")
                     ?? "# リリースノート\n\nリリースノート (ReleaseNotes.md) を読み込めませんでした。";

            var v = string.IsNullOrWhiteSpace(version) ? string.Empty : $"v{version}";
            md = md.Replace("{version}", v);

            Viewer.Document = MarkdownRenderer.ToFlowDocument(md);
        }

        private static string ReadEmbedded(string name)
        {
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                using var stream = asm.GetManifestResourceStream($"{asm.GetName().Name}.Resources.{name}");
                if (stream == null) return null;

                using var reader = new StreamReader(stream, Encoding.UTF8);
                return reader.ReadToEnd();
            }
            catch (Exception e)
            {
                Log.Warning(e, "ReleaseNotes.md の読み込みに失敗しました");
                return null;
            }
        }
    }
}
