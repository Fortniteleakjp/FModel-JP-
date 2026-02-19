using System.Windows;
using System.Windows.Controls;

namespace FModel.Views
{
    public partial class PropertyEditDialog : AdonisUI.Controls.AdonisWindow
    {
        public string PropertyName { get; private set; }
        public string PropertyType { get; private set; }
        public string PropertyValue { get; private set; }
        public string EnumType { get; private set; }

        public PropertyEditDialog()
        {
            InitializeComponent();
            Owner = Application.Current.MainWindow;
            PropertyTypeComboBox.SelectedIndex = 0;
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            PropertyName = PropertyNameTextBox.Text;
            PropertyType = (PropertyTypeComboBox.SelectedItem as ComboBoxItem)?.Content.ToString();
            PropertyValue = PropertyValueTextBox.Text;
            EnumType = EnumTypeTextBox.Text;

            if (string.IsNullOrWhiteSpace(PropertyName) || string.IsNullOrWhiteSpace(PropertyType) || string.IsNullOrWhiteSpace(PropertyValue))
            {
                MessageBox.Show("Property Name, Type, and Value are required.", "Input Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            DialogResult = true;
        }

        private void PropertyTypeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var isByteProp = (PropertyTypeComboBox.SelectedItem as ComboBoxItem)?.Content.ToString() == "BytePropertyData";
            EnumTypeLabel.Visibility = isByteProp ? Visibility.Visible : Visibility.Collapsed;
            EnumTypeTextBox.Visibility = isByteProp ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}