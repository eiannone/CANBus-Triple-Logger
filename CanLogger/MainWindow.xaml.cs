using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using CanBusTriple;
using CanLogger.Properties;
using Microsoft.Win32;

namespace CanLogger
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow
    {
        const int RECEIVE_BUFFER_SIZE = 10000;
        const int DISPLAY_BUFFER_SIZE = 5000;
        const int REFRESH_INTERVAL = 200;
        private readonly CBTController _cbt;
        private int _bus;
        private int _filter1;
        private int _mask1;
        private readonly CanMessageBuffer _buffer;
        private readonly DispatcherTimer _timer;
        private StreamWriter _sw;

        public MainWindow()
        {
            InitializeComponent();
            Refresh_Ports(null, null);
            _cbt = new CBTController((string)PortList.SelectedItem);            
            _buffer = new CanMessageBuffer(RECEIVE_BUFFER_SIZE);
            _timer = new DispatcherTimer { IsEnabled = false, Interval = TimeSpan.FromMilliseconds(REFRESH_INTERVAL)};
            _timer.Tick += (sender, e) => { if (!_buffer.IsEmpty) LoadDataGrid(); };
            Closing += MainWindow_Closing;

            BtConnect.IsChecked = BtFilter.IsChecked = BtSave.IsChecked = false;
            LoggingDisabled();
            DgLog.ItemsSource = new ObservableCollection<CanMessage>();
        }

        async void MainWindow_Closing(object sender, CancelEventArgs e)
        {
            if (!_cbt.Connected && !_cbt.Busy) return;
            if (_cbt.Busy) await _cbt.CancelCommand(true);
            await _cbt.Disconnect();
        }

        private void btSaveFile_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new SaveFileDialog {
                Filter = "File .csv|*.csv",
                DefaultExt = "csv",
                Title = "Select file for saving log"
            };
            if (openFileDialog.ShowDialog() == true) FilePath.Text = openFileDialog.FileName;
        }

        private void Refresh_Ports(object sender, RoutedEventArgs e)
        {
            PortList.Items.Clear(); //DropDownItems.Clear();
            var comPorts = SerialPort.GetPortNames();
            if (comPorts.Length == 0) {
                PortList.Items.Add("----");
                PortList.SelectedIndex = 0;
                PortList.IsEnabled = BtConnect.IsEnabled = false;
            }
            else {
                foreach (var port in comPorts) PortList.Items.Add(port);
                PortList.SelectedItem = comPorts.Contains(Settings.Default.PortName) ? Settings.Default.PortName : comPorts[0];
                PortList.IsEnabled = BtConnect.IsEnabled = true;
            }
        }

        private async void btConnect_Checked(object sender, RoutedEventArgs e)
        {
            ImgConnect.Opacity = 0.5;
            LbConnect.Content = "Connecting...";
            BtConnect.IsEnabled = PortList.IsEnabled = BtRefreshPorts.IsEnabled = false;
            ExceptionDispatchInfo capturedException = null;
            try {
                _cbt.Connect();
                var info = await _cbt.GetSystemInfo();
                if (!info.ContainsKey("name") || !info.ContainsKey("version"))
                    throw new Exception("Unable to identify CANBus Triple");
                
                LbInfo.Content = info["name"] + " " + info["version"];
                ImgConnect.Source = (ImageSource)Resources["ImgDisconnect"];
                ImgConnect.Opacity = 1;
                LbConnect.Content = "Connected";
                BtConnect.ToolTip = "Disconnect";
                BtConnect.IsEnabled = true;
            }
            catch (Exception ex) {
                capturedException = ExceptionDispatchInfo.Capture(ex);
            }
            if (capturedException == null) return;

            var task = _cbt.Connected? _cbt.Disconnect() : Task.Delay(5);
            MessageBox.Show(capturedException.SourceException.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            await task;
            BtConnect.IsChecked = false;
        }

        private async void btConnect_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_cbt.Connected) await _cbt.Disconnect();
            
            LbInfo.Content = "";
            ImgConnect.Source = (ImageSource)Resources["ImgDisconnect"];
            ImgConnect.Opacity = 1;
            LbConnect.Content = "Disconnected";
            BtConnect.ToolTip = "Connect";
            BtRefreshPorts.IsEnabled = true;
            if ((string)PortList.SelectedItem != "----") {
                PortList.IsEnabled = BtConnect.IsEnabled = true;
            }
        }

        private async void btLog_Checked(object sender, RoutedEventArgs e)
        {
            ExceptionDispatchInfo capturedException = null;
            try {
                _cbt.CanMessageReceived += cbt_CanMessageReceived;
                LoggingEnabled();
                if (BtConnect.IsChecked != true) BtConnect.IsChecked = true;
                _timer.Start();
                if (BtSave.IsChecked == true) StartLog();
                if (BtFilter.IsChecked == true && (_filter1 > 0 || CbMask.IsChecked == true))  {
                    await _cbt.EnableLogWithMask(_bus, _filter1, _mask1);
                }
                else {
                    await _cbt.EnableLog(_bus);
                }
            }
            catch (Exception ex) {
                capturedException = ExceptionDispatchInfo.Capture(ex);
            }
            if (capturedException == null) return;
            
            var task = _cbt.Busy ? _cbt.CancelCommand() : Task.Delay(5);
            LoggingDisabled();
            _timer.Stop();
            TerminateLog();
            MessageBox.Show(capturedException.SourceException.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            await task;
        }

        private async void btLog_Unchecked(object sender, RoutedEventArgs e)
        {
            ExceptionDispatchInfo captureEx = null;
            try {
                _cbt.CanMessageReceived -= cbt_CanMessageReceived;
                await _cbt.DisableLog(_bus);
            }
            catch (Exception ex) {
                captureEx = ExceptionDispatchInfo.Capture(ex);
            }

            var task = _cbt.Busy ? _cbt.CancelCommand() : Task.Delay(5);
            LoggingDisabled();
            _timer.Stop();
            TerminateLog();

            if (captureEx != null)          
                MessageBox.Show(captureEx.SourceException.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            
            await task;
        }

        private void TerminateLog()
        {
            if (_sw == null) return;
            _sw.Flush();
            _sw.Close();
            _sw.Dispose();
            _sw = null;
        }

        private void StartLog()
        {
            if (FilePath.Text.Length == 0) return;
            var path = FilePath.Text.Trim();
            if (File.Exists(path)) {
                _sw = new StreamWriter(File.Open(path, FileMode.Append));
            }
            else {
                try {
                    _sw = new StreamWriter(File.OpenWrite(path));
                    _sw.WriteLine("\"Time\";\"Msg ID\";\"Data\";\"Bus RX\";\"Notes\"");
                }
                catch (Exception) {
                    // ignored
                }
            }
        }

        private void LoggingEnabled()
        {
            _bus = int.Parse(((ComboBoxItem)CbBus.SelectedItem).Content.ToString().Substring(4));
            CbBus.IsEnabled = false;
            _filter1 = (TbFilter.Text.Trim() != "") ? Convert.ToInt32(TbFilter.Text.Replace(" ", ""), 16) : 0;
            _mask1 = (TbMask.Text.Trim() != "") ? Convert.ToInt32(TbMask.Text.Replace(" ", ""), 16) : 0;
            BtFilter.IsEnabled = TbMask.IsEnabled = TbFilter.IsEnabled = false;
            BtSave.IsEnabled = FilePath.IsEnabled = false;
        }

        private void LoggingDisabled()
        {
            CbBus.IsEnabled = true;
            BtFilter.IsEnabled = TbMask.IsEnabled = TbFilter.IsEnabled = true;
            BtSave.IsEnabled = FilePath.IsEnabled = true;
        }

        private void LoadDataGrid()
        {
            var msgs = _buffer.GetLastMessages(DISPLAY_BUFFER_SIZE);
            foreach (var msg in msgs) {
                ((ObservableCollection<CanMessage>)DgLog.ItemsSource).Add(msg);
                if (DgLog.Items.Count > DISPLAY_BUFFER_SIZE)
                    ((ObservableCollection<CanMessage>)DgLog.ItemsSource).RemoveAt(0);
            }
            
            // Autoscroll to last item
            var border = VisualTreeHelper.GetChild(DgLog, 0) as Decorator;
            if (border != null) {
                var scroll = border.Child as ScrollViewer;
                if (scroll != null) scroll.ScrollToEnd();
            }
        }

        void cbt_CanMessageReceived(CanMessage msg)
        {
            if (msg.Bus != _bus) return;

            if (_sw != null) {
                _sw.WriteLine("\"" + msg.Time + "\";"
                    + "\"" + String.Format("{0:X3}", msg.Id) + "\";"
                    + "\"" + msg.HexData + "\";"
                    + msg.Status + ";"
                    + "\"\"");
            }
            _buffer.AddMessage(msg);
        }

        private void PortList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count == 0) return;
            if ((string) e.AddedItems[0] != Settings.Default.PortName) {
                Settings.Default.PortName = e.AddedItems[0].ToString();
                Settings.Default.Save();
            }
            if (_cbt != null) _cbt.SetComPort(e.AddedItems[0].ToString());
        }

        private void BtClear_OnClick(object sender, RoutedEventArgs e)
        {
            ((ObservableCollection<CanMessage>)DgLog.ItemsSource).Clear();
        }
    }
}
