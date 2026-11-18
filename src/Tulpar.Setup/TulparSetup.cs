using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("TULPAR Mission Planner Kurulum Sihirbazı")]
[assembly: AssemblyDescription("TULPAR Mission Planner Windows Kurulum Paketi")]
[assembly: AssemblyCompany("TULPAR")]
[assembly: AssemblyProduct("TULPAR Mission Planner")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace Tulpar.Setup
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            bool silent = false;
            bool uninstall = false;
            string customDir = null;

            if (args != null)
            {
                for (int i = 0; i < args.Length; i++)
                {
                    string a = args[i] ?? string.Empty;
                    if (string.Equals(a, "--silent", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(a, "/S", StringComparison.OrdinalIgnoreCase))
                    {
                        silent = true;
                    }
                    else if (string.Equals(a, "--uninstall", StringComparison.OrdinalIgnoreCase))
                    {
                        uninstall = true;
                    }
                    else if (a.StartsWith("--dir=", StringComparison.OrdinalIgnoreCase))
                    {
                        customDir = a.Substring(6).Trim('"');
                    }
                }
            }

            string defaultDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs",
                "Tulpar Mission Planner");

            if (uninstall)
            {
                RunUninstall(customDir ?? defaultDir, silent);
                return 0;
            }

            if (silent)
            {
                InstallerEngine.Install(
                    customDir ?? defaultDir,
                    createDesktopShortcut: true,
                    createStartMenuShortcut: true,
                    launchAfter: false,
                    progressCallback: null);
                return 0;
            }

            Application.Run(new SetupForm(customDir ?? defaultDir));
            return 0;
        }

        private static void RunUninstall(string targetDir, bool silent)
        {
            if (!silent)
            {
                DialogResult dr = MessageBox.Show(
                    "TULPAR Mission Planner bilgisayarınızdan kaldırılsın mı?",
                    "TULPAR // Kaldır",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);
                if (dr != DialogResult.Yes)
                {
                    return;
                }
            }

            try
            {
                foreach (Process p in Process.GetProcessesByName("Tulpar.App"))
                {
                    try { p.Kill(); p.WaitForExit(2000); } catch { }
                }

                string desktopLnk = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                    "TULPAR Mission Planner.lnk");
                if (File.Exists(desktopLnk)) File.Delete(desktopLnk);

                string startMenuLnk = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                    "TULPAR Mission Planner.lnk");
                if (File.Exists(startMenuLnk)) File.Delete(startMenuLnk);

                Registry.CurrentUser.DeleteSubKeyTree(
                    @"Software\Microsoft\Windows\CurrentVersion\Uninstall\TulparMissionPlanner",
                    throwOnMissingSubKey: false);

                if (Directory.Exists(targetDir))
                {
                    try { Directory.Delete(targetDir, recursive: true); } catch { }
                }

                if (!silent)
                {
                    MessageBox.Show(
                        "TULPAR Mission Planner başarıyla kaldırıldı.",
                        "TULPAR // Kaldır",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                if (!silent)
                {
                    MessageBox.Show("Kaldırma hatası: " + ex.Message, "TULPAR", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }
    }

    internal static class InstallerEngine
    {
        public static void Install(
            string installDir,
            bool createDesktopShortcut,
            bool createStartMenuShortcut,
            bool launchAfter,
            Action<int, string> progressCallback)
        {
            foreach (Process p in Process.GetProcessesByName("Tulpar.App"))
            {
                try { p.Kill(); p.WaitForExit(2000); } catch { }
            }

            Directory.CreateDirectory(installDir);

            Assembly asm = Assembly.GetExecutingAssembly();
            using (Stream payloadStream = asm.GetManifestResourceStream("TulparPayload.zip"))
            {
                if (payloadStream == null)
                {
                    throw new InvalidOperationException("Kurulum paketi verisi (TulparPayload.zip) bulunamadı.");
                }

                using (ZipArchive archive = new ZipArchive(payloadStream, ZipArchiveMode.Read))
                {
                    int total = Math.Max(1, archive.Entries.Count);
                    int index = 0;

                    foreach (ZipArchiveEntry entry in archive.Entries)
                    {
                        index++;
                        string relPath = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                        string destPath = Path.Combine(installDir, relPath);

                        if (string.IsNullOrEmpty(entry.Name))
                        {
                            Directory.CreateDirectory(destPath);
                            continue;
                        }

                        string parent = Path.GetDirectoryName(destPath);
                        if (!string.IsNullOrEmpty(parent))
                        {
                            Directory.CreateDirectory(parent);
                        }

                        using (Stream inStream = entry.Open())
                        using (FileStream outStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None))
                        {
                            inStream.CopyTo(outStream);
                        }

                        int pct = Math.Min(95, (int)((index * 95L) / total));
                        if (progressCallback != null && (index % 5 == 0 || index == total))
                        {
                            progressCallback(pct, "Dosyalar kuruluyor: " + entry.Name);
                        }
                    }
                }
            }

            string exePath = Path.Combine(installDir, "Tulpar.App.exe");
            if (createDesktopShortcut)
            {
                if (progressCallback != null) progressCallback(96, "Masaüstü kısayolu oluşturuluyor...");
                string desktopLnk = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                    "TULPAR Mission Planner.lnk");
                CreateShortcut(desktopLnk, exePath, installDir, "TULPAR Mission Planner Ground Control Station");
            }

            if (createStartMenuShortcut)
            {
                if (progressCallback != null) progressCallback(98, "Başlat Menüsü kısayolu oluşturuluyor...");
                string startMenuLnk = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                    "TULPAR Mission Planner.lnk");
                CreateShortcut(startMenuLnk, exePath, installDir, "TULPAR Mission Planner Ground Control Station");
            }

            RegisterUninstaller(installDir, exePath);
            if (progressCallback != null) progressCallback(100, "Kurulum tamamlandı!");

            if (launchAfter && File.Exists(exePath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = exePath,
                    WorkingDirectory = installDir,
                    UseShellExecute = true
                });
            }
        }

        private static void RegisterUninstaller(string installDir, string exePath)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Uninstall\TulparMissionPlanner"))
                {
                    if (key != null)
                    {
                        key.SetValue("DisplayName", "TULPAR Mission Planner");
                        key.SetValue("DisplayVersion", "1.0.0");
                        key.SetValue("Publisher", "TULPAR Systems");
                        key.SetValue("DisplayIcon", exePath);
                        key.SetValue("InstallLocation", installDir);
                        key.SetValue("UninstallString", "\"" + Application.ExecutablePath + "\" --uninstall --dir=\"" + installDir + "\"");
                        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                    }
                }
            }
            catch
            {
            }
        }

        private static void CreateShortcut(string shortcutPath, string targetExe, string workingDir, string description)
        {
            try
            {
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) return;
                dynamic shell = Activator.CreateInstance(shellType);
                dynamic shortcut = shell.CreateShortcut(shortcutPath);
                shortcut.TargetPath = targetExe;
                shortcut.WorkingDirectory = workingDir;
                shortcut.Description = description;
                shortcut.IconLocation = targetExe + ",0";
                shortcut.Save();
            }
            catch
            {
            }
        }
    }

    internal sealed class SetupForm : Form
    {
        private readonly TextBox _dirBox;
        private readonly CheckBox _desktopChk;
        private readonly CheckBox _startMenuChk;
        private readonly CheckBox _launchChk;
        private readonly ProgressBar _progressBar;
        private readonly Label _statusLabel;
        private readonly Button _installBtn;
        private readonly Button _cancelBtn;
        private readonly Button _browseBtn;

        public SetupForm(string defaultInstallDir)
        {
            Text = "TULPAR MISSION PLANNER // KURULUM SİHİRBAZI";
            ClientSize = new Size(540, 360);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(5, 5, 7);
            ForeColor = Color.White;
            Font = new Font("Segoe UI", 9.5f, FontStyle.Regular);

            try
            {
                Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch { }

            Panel headerPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 68,
                BackColor = Color.FromArgb(10, 10, 14)
            };

            Label titleLbl = new Label
            {
                Text = "TULPAR MISSION PLANNER",
                Font = new Font("Segoe UI", 14f, FontStyle.Bold),
                ForeColor = Color.White,
                Location = new Point(20, 12),
                AutoSize = true
            };

            Label subLbl = new Label
            {
                Text = "Yer Kontrol İstasyonu · Windows x64 Nihai Kurulum Paketi (v1.0.0)",
                Font = new Font("Segoe UI", 9f, FontStyle.Regular),
                ForeColor = Color.FromArgb(160, 160, 170),
                Location = new Point(22, 40),
                AutoSize = true
            };

            headerPanel.Controls.Add(titleLbl);
            headerPanel.Controls.Add(subLbl);
            Controls.Add(headerPanel);

            Label dirTitle = new Label
            {
                Text = "KURULUM KLASÖRÜ:",
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(160, 160, 170),
                Location = new Point(22, 86),
                AutoSize = true
            };
            Controls.Add(dirTitle);

            _dirBox = new TextBox
            {
                Text = defaultInstallDir,
                Location = new Point(22, 108),
                Size = new Size(396, 26),
                BackColor = Color.FromArgb(16, 16, 22),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle
            };
            Controls.Add(_dirBox);

            _browseBtn = new Button
            {
                Text = "GÖZAT...",
                Location = new Point(426, 106),
                Size = new Size(92, 27),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(16, 16, 22),
                ForeColor = Color.White
            };
            _browseBtn.FlatAppearance.BorderColor = Color.FromArgb(90, 90, 100);
            _browseBtn.Click += OnBrowseClick;
            Controls.Add(_browseBtn);

            _desktopChk = new CheckBox
            {
                Text = "Masaüstüne kısayol oluştur (TULPAR Mission Planner)",
                Checked = true,
                Location = new Point(24, 150),
                AutoSize = true,
                ForeColor = Color.White
            };
            Controls.Add(_desktopChk);

            _startMenuChk = new CheckBox
            {
                Text = "Başlat Menüsü'ne kısayol ekle",
                Checked = true,
                Location = new Point(24, 178),
                AutoSize = true,
                ForeColor = Color.White
            };
            Controls.Add(_startMenuChk);

            _launchChk = new CheckBox
            {
                Text = "Kurulum tamamlandığında TULPAR Mission Planner'ı başlat",
                Checked = true,
                Location = new Point(24, 206),
                AutoSize = true,
                ForeColor = Color.FromArgb(52, 199, 89)
            };
            Controls.Add(_launchChk);

            _statusLabel = new Label
            {
                Text = "Kuruluma hazır.",
                Location = new Point(22, 246),
                Size = new Size(496, 20),
                ForeColor = Color.FromArgb(180, 180, 190)
            };
            Controls.Add(_statusLabel);

            _progressBar = new ProgressBar
            {
                Location = new Point(22, 270),
                Size = new Size(496, 18),
                Minimum = 0,
                Maximum = 100,
                Value = 0
            };
            Controls.Add(_progressBar);

            _cancelBtn = new Button
            {
                Text = "İPTAL",
                Location = new Point(290, 308),
                Size = new Size(100, 34),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(16, 16, 22),
                ForeColor = Color.White
            };
            _cancelBtn.FlatAppearance.BorderColor = Color.FromArgb(90, 90, 100);
            _cancelBtn.Click += (s, e) => Close();
            Controls.Add(_cancelBtn);

            _installBtn = new Button
            {
                Text = "KURULUMU BAŞLAT",
                Location = new Point(400, 308),
                Size = new Size(118, 34),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.White,
                ForeColor = Color.Black,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold)
            };
            _installBtn.Click += OnInstallClick;
            Controls.Add(_installBtn);
        }

        private void OnBrowseClick(object sender, EventArgs e)
        {
            using (FolderBrowserDialog fbd = new FolderBrowserDialog())
            {
                fbd.Description = "TULPAR Mission Planner kurulum klasörünü seçin";
                fbd.SelectedPath = _dirBox.Text;
                if (fbd.ShowDialog(this) == DialogResult.OK)
                {
                    _dirBox.Text = fbd.SelectedPath;
                }
            }
        }

        private async void OnInstallClick(object sender, EventArgs e)
        {
            string targetDir = _dirBox.Text.Trim();
            if (string.IsNullOrEmpty(targetDir))
            {
                MessageBox.Show(this, "Lütfen geçerli bir kurulum klasörü seçin.", "TULPAR", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _installBtn.Enabled = false;
            _browseBtn.Enabled = false;
            _dirBox.Enabled = false;
            _desktopChk.Enabled = false;
            _startMenuChk.Enabled = false;
            _launchChk.Enabled = false;

            bool desktop = _desktopChk.Checked;
            bool startMenu = _startMenuChk.Checked;
            bool launch = _launchChk.Checked;

            try
            {
                await Task.Run(() =>
                {
                    InstallerEngine.Install(
                        targetDir,
                        desktop,
                        startMenu,
                        launch,
                        (pct, msg) =>
                        {
                            BeginInvoke(new Action(() =>
                            {
                                _progressBar.Value = Math.Max(0, Math.Min(100, pct));
                                _statusLabel.Text = msg;
                            }));
                        });
                });

                _progressBar.Value = 100;
                _statusLabel.Text = "Kurulum başarıyla tamamlandı!";
                MessageBox.Show(
                    this,
                    "TULPAR Mission Planner başarıyla kuruldu!\n\nKonum: " + targetDir,
                    "TULPAR // Kurulum Tamamlandı",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    this,
                    "Kurulum sırasında hata oluştu:\n" + ex.Message,
                    "TULPAR // Kurulum Hatası",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                _installBtn.Enabled = true;
                _browseBtn.Enabled = true;
                _dirBox.Enabled = true;
            }
        }
    }
}
