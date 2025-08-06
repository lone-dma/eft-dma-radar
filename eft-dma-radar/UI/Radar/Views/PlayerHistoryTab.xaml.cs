﻿using eft_dma_radar.UI.Radar.ViewModels;
using System.Windows.Controls;
using System.Windows.Input;

namespace eft_dma_radar.UI.Radar.Views
{
    /// <summary>
    /// Interaction logic for PlayerHistoryTab.xaml
    /// </summary>
    public partial class PlayerHistoryTab : UserControl
    {
        public PlayerHistoryViewModel ViewModel { get; }
        public PlayerHistoryTab()
        {
            InitializeComponent();
            DataContext = ViewModel = new PlayerHistoryViewModel();
        }

        private void DataGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => ViewModel.HandleDoubleClick();
    }
}
