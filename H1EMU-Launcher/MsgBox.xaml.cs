﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace H1EMU_Launcher
{
    /// <summary>
    /// Interaction logic for MsgBox.xaml
    /// </summary>

    public partial class MsgBox : Window
    {
        public MsgBox()
        {
            InitializeComponent();

            DoubleAnimation fadeAnimation = new DoubleAnimation();
            fadeAnimation.Duration = TimeSpan.FromMilliseconds(20d);
            fadeAnimation.From = 0.0d;
            fadeAnimation.To = 1.0d;
            MainMsgBox.BeginAnimation(OpacityProperty, fadeAnimation);
        }

        private void MainMsgBox_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            e.Cancel = true;

            DoubleAnimation fadeAnimation = new DoubleAnimation();
            fadeAnimation.Duration = TimeSpan.FromMilliseconds(100d);
            fadeAnimation.From = 1.0d;
            fadeAnimation.To = 0.0d;
            MainMsgBox.BeginAnimation(OpacityProperty, fadeAnimation);

            while (MainMsgBox.Opacity != 0) { System.Windows.Forms.Application.DoEvents(); }

            e.Cancel = false;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private void OkButton(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private void Grid_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            this.DragMove();
        }
    }
}
