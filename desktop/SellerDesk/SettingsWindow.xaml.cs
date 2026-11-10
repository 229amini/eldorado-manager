using System.Media;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SellerDesk
{
    public partial class SettingsWindow : Window
    {
        private readonly Config existing;
        private readonly bool keysOnly;

        /// <param name="keysOnly">Asks only for the Eldorado key, because the dashboard needs nothing else.</param>
        public SettingsWindow(Config existing, bool keysOnly = false)
        {
            this.existing = existing ?? new Config { Topic = "" };   // a shared default topic would ring for strangers' messages
            this.keysOnly = keysOnly;
            Skin.Use(this.existing.Theme);
            InitializeComponent();
            foreach (var skin in Skin.All)
            {
                var choice = new RadioButton
                {
                    Content = skin.Label,
                    GroupName = "theme",
                    Style = (Style)FindResource("Choice"),
                    IsChecked = skin == Skin.Current,
                };
                choice.Checked += (_, __) => PickTheme(skin.Name);
                Themes.Children.Add(choice);
            }
            UrlBox.Text = this.existing.Url;
            TopicBox.Text = this.existing.Topic;
            TokenBox.Password = this.existing.Token;
            SellerIdBox.Text = this.existing.SellerId;
            SellerSecretBox.Password = this.existing.SellerSecret;
            HubUrlBox.Text = this.existing.HubUrl;
            HubTokenBox.Password = this.existing.HubToken;
            VolumeBox.Value = this.existing.Volume;
            (this.existing.Ring ? RingOn : RingOff).IsChecked = true;
            var firstRun = this.existing.Topic.Length == 0 || existing == null;
            if (firstRun) SaveButton.Content = "Save and start";
            Open(keysOnly ? "key" : firstRun ? "alarms" : "general");
            if (keysOnly)
            {
                Tabs.Visibility = Visibility.Collapsed;
                GeneralPart.Visibility = Visibility.Collapsed;
                AlarmPart.Visibility = Visibility.Collapsed;
            }
        }

        public void Open(string part) =>
            (part == "alarms" ? AlarmTab : part == "key" ? KeyTab : GeneralTab).IsChecked = true;

        private void PartChanged(object sender, RoutedEventArgs e)
        {
            GeneralPart.Visibility = GeneralTab.IsChecked == true ? Visibility.Visible : Visibility.Hidden;
            AlarmPart.Visibility = AlarmTab.IsChecked == true ? Visibility.Visible : Visibility.Hidden;
            KeyPart.Visibility = KeyTab.IsChecked == true ? Visibility.Visible : Visibility.Hidden;
        }

        /// <summary>A theme needs no connection test, so it saves at once and an open dashboard follows it.</summary>
        private void PickTheme(string name)
        {
            Skin.Use(name);
            KeepTheme(name);
        }

        private static void KeepTheme(string name)
        {
            var saved = Config.Load();
            if (saved == null) return;
            saved.Theme = name;
            saved.Save();
        }

        private void VolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
            VolumeText.Text = (int)e.NewValue + "%";

        /// <summary>Plays the critical alarm, the loudest sound the app makes.</summary>
        private void TestSound(object sender, RoutedEventArgs e)
        {
            using (var player = new SoundPlayer(Sounds.Load("alarm.wav", (int)VolumeBox.Value))) player.Play();
        }

        public Config Result { get; private set; }

        private void Drag(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        }

        private async void Save(object sender, RoutedEventArgs e)
        {
            var config = new Config
            {
                Url = UrlBox.Text.Trim().TrimEnd('/'),
                Topic = TopicBox.Text.Trim(),
                Token = TokenBox.Password.Trim(),
                Sound = existing.Sound,
                Volume = (int)VolumeBox.Value,
                Ring = RingOff.IsChecked != true,
                Theme = Skin.Current.Name,
                SellerId = SellerIdBox.Text.Trim(),
                SellerSecret = SellerSecretBox.Password.Trim(),
                HubUrl = HubUrlBox.Text.Trim().TrimEnd('/'),
                HubToken = HubTokenBox.Password.Trim(),
                Zoom = existing.Zoom,
            };
            if (!keysOnly && !config.IsSetUp)
            {
                Open("alarms");
                Show("Enter a topic for alarms, or an Eldorado key for the dashboard.");
                return;
            }
            if (!keysOnly && config.HasAlarms && !Regex.IsMatch(config.Topic, "^[-_A-Za-z0-9]{1,64}$"))
            {
                Open("alarms");
                Show("A topic is one word: letters, numbers, - or _. For example: eldorado-7f3kq9x2mz");
                return;
            }
            if (keysOnly && !config.HasSellerKey)
            {
                Show("Enter both the client ID and the client secret.");
                return;
            }
            SaveButton.IsEnabled = false;
            Show("Testing the connection");
            var error = await Task.Run(() => Test(config));
            SaveButton.IsEnabled = true;
            if (error != null)
            {
                Show(error);
                return;
            }
            config.Save();
            Result = config;
            DialogResult = true;
        }

        private string Test(Config config)
        {
            var error = keysOnly || !config.HasAlarms ? null : config.TestConnection();
            if (error == null && config.HasHub)
            {
                try
                {
                    new Hub(config.HubUrl, config.HubToken).Ledger();
                }
                catch (MarketError problem)
                {
                    return "Shared server: " + problem.Message;
                }
            }
            if (error != null || !config.HasSellerKey) return error;
            try
            {
                new Market(config.SellerId, config.SellerSecret).Offers();
                return null;
            }
            catch (MarketError problem)
            {
                return problem.Message;
            }
        }

        private void Show(string text)
        {
            Status.Text = text;
            Status.Visibility = Visibility.Visible;
        }

        private void Cancel(object sender, RoutedEventArgs e)
        {
            PickTheme(existing.Theme);
            DialogResult = false;
        }
    }
}
