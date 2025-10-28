using System;
using System.Windows.Forms;
using LibVLCSharp.Shared;

namespace MxfPlayer
{
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            try
            {
                Core.Initialize(); // 初始化 LibVLC
                Application.Run(new Form1());
            }
            catch (Exception ex)
            {
                MessageBox.Show("程式啟動失敗：\n" + ex.Message, "錯誤", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
