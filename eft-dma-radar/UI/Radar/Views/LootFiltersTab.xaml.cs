﻿using eft_dma_radar.UI.Radar.ViewModels;
using System.Windows.Controls;

namespace eft_dma_radar.UI.Radar.Views
{
    /// <summary>
    /// Interaction logic for LootFiltersTab.xaml
    /// </summary>
    public partial class LootFiltersTab : UserControl
    {
        public LootFiltersViewModel ViewModel { get; }

        public LootFiltersTab()
        {
            InitializeComponent();
            DataContext = ViewModel = new LootFiltersViewModel(this);
        }
    }
}
