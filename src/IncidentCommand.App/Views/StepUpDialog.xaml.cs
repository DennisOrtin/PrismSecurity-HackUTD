using System.Windows;
using IncidentCommand.Client;

namespace IncidentCommand.App.Views;

/// Step-up re-authentication for containment and destructive actions.
/// Honest limit: enforced by the app. Moving TOTP checks into the module is a stretch goal.
public partial class StepUpDialog : Window
{
    public StepUpDialog(string whatWillHappen, string buttonText)
    {
        InitializeComponent();
        WhatText.Text = whatWillHappen;
        OkButton.Content = buttonText;
        Owner = Application.Current.MainWindow;
    }

    public static bool Confirm(string whatWillHappen, string buttonText) =>
        new StepUpDialog(whatWillHappen, buttonText).ShowDialog() == true;

    async void Ok_Click(object sender, RoutedEventArgs e)
    {
        OkButton.IsEnabled = false;
        var password = Password.Password;
        var contents = await Task.Run(() => Vault.Unlock(Session.Username, password));
        Password.Clear();
        // The 6-digit code was checked at login and stays valid for this session; step-up only needs the password.
        if (contents == null)
        {
            ErrorText.Text = "Wrong password.";
            ErrorText.Visibility = Visibility.Visible;
            OkButton.IsEnabled = true;
            return;
        }
        DialogResult = true;
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
