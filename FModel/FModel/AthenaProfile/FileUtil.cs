using System.Windows.Forms;

public static class FileUtil
{
    public static string SelectSaveFile()
    {
        string result = null;
        using (var dialog = new SaveFileDialog())
        {
            dialog.Title = "保存先を選択してください";
            dialog.Filter = "JSON Files|*.json";
            dialog.FileName = "athena.json";
            dialog.OverwritePrompt = true;

            if (dialog.ShowDialog() == DialogResult.OK)
                result = dialog.FileName;
        }
        return result;
    }
}
