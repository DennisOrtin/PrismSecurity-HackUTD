using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using IncidentCommand.Client;
using QRCoder;

namespace IncidentCommand.App.Views;

public partial class LockWindow : Window
{
    string _pendingSecret = "";

    public LockWindow()
    {
        InitializeComponent();
        // Closing the lock screen without logging in quits the app.
        Closed += (_, _) =>
        {
            if (!Application.Current.Windows.OfType<MainWindow>().Any()) Application.Current.Shutdown();
        };
        // Usernames are typed, never listed: the lock screen shouldn't tell a passer-by who has an account.
        if (Vault.Usernames().Count == 0) ShowSetup();
        else UserBox.Focus();
    }

    // ---------- Log in ----------

    async void Login_Click(object sender, RoutedEventArgs e)
    {
        var username = UserBox.Text.Trim().ToLowerInvariant();
        if (username.Length == 0) { ShowError("Enter your username."); return; }
        if (LoginGuard.LockedFor(username) is { } wait)
        {
            ShowError($"Too many failed attempts. Try again in {Math.Ceiling(wait.TotalMinutes)} minute(s).");
            return;
        }

        LoginButton.IsEnabled = false;
        try
        {
            var password = LoginPassword.Password;
            // Same message and roughly the same time whether or not the username exists.
            var exists = File.Exists(Vault.PathFor(username));
            var contents = exists
                ? await Task.Run(() => Vault.Unlock(username, password))   // Argon2 is slow on purpose
                : await Task.Delay(700).ContinueWith(_ => (VaultContents?)null);
            if (contents == null || !TotpHelper.Verify(contents.TotpSecret, LoginCode.Text))
            {
                LoginGuard.Fail(username);
                ShowError("Wrong username, password or code.");
                return;
            }
            LoginGuard.Succeed(username);
            await Session.StartAsync(contents.Token);
            App.ShowMain();
        }
        catch (Exception ex) { ShowError(ex.Message); }
        finally
        {
            LoginPassword.Clear();
            LoginCode.Clear();
            LoginButton.IsEnabled = true;
        }
    }

    // ---------- First run ----------

    void ShowSetup()
    {
        LoginPanel.Visibility = Visibility.Collapsed;
        SetupPanel.Visibility = Visibility.Visible;
        _pendingSecret = TotpHelper.NewSecret();
        SetupQr.Source = Qr(TotpHelper.ProvisioningUri(_pendingSecret, "commander-01"));
    }

    async void Setup_Click(object sender, RoutedEventArgs e)
    {
        if (!CheckNewPassword(SetupPassword.Password, SetupPassword2.Password)) return;
        if (!TotpHelper.Verify(_pendingSecret, SetupCode.Text)) { ShowError("That code doesn't match. Check your phone's clock and try again."); return; }
        if (string.IsNullOrWhiteSpace(SetupName.Text)) { ShowError("Enter your name."); return; }

        SetupButton.IsEnabled = false;
        try
        {
            // Fresh connection with no token: the local server mints a new identity for us.
            var (conn, token) = await Session.OpenAsync(null);
            var done = new TaskCompletionSource<string?>();
            conn.Reducers.OnClaimCommander += (ctx, _) => done.TrySetResult(Stdb.Failure(ctx));
            conn.Reducers.ClaimCommander(SetupName.Text.Trim());
            var failure = await done.Task;
            Session.Close(conn);
            if (failure != null) { ShowError(failure); return; }

            var password = SetupPassword.Password;
            await Task.Run(() => Vault.Save(new VaultContents("commander-01", token, _pendingSecret), password));
            await Session.StartAsync(token);
            App.ShowMain();
        }
        catch (Exception ex) { ShowError(ex.Message); }
        finally { SetupButton.IsEnabled = true; }
    }

    // ---------- Enroll ----------

    void ShowEnroll_Click(object sender, RoutedEventArgs e)
    {
        LoginPanel.Visibility = Visibility.Collapsed;
        EnrollPanel.Visibility = Visibility.Visible;
        _pendingSecret = TotpHelper.NewSecret();
        EnrollQr.Source = Qr(TotpHelper.ProvisioningUri(_pendingSecret, "new account"));
    }

    void ShowLogin_Click(object sender, RoutedEventArgs e)
    {
        EnrollPanel.Visibility = Visibility.Collapsed;
        LoginPanel.Visibility = Visibility.Visible;
    }

    async void Enroll_Click(object sender, RoutedEventArgs e)
    {
        if (!CheckNewPassword(EnrollPassword.Password, EnrollPassword2.Password)) return;
        if (!TotpHelper.Verify(_pendingSecret, EnrollTotp.Text)) { ShowError("That code doesn't match. Check your phone's clock and try again."); return; }

        EnrollButton.IsEnabled = false;
        try
        {
            var (conn, token) = await Session.OpenAsync(null);

            // Learn the username the module assigns us, via the MyUser view.
            var assigned = new TaskCompletionSource<string>();
            conn.Db.MyUser.OnInsert += (_, u) => assigned.TrySetResult(u.Username);
            var subscribed = new TaskCompletionSource();
            conn.SubscriptionBuilder().OnApplied(_ => subscribed.TrySetResult()).AddQuery(q => q.From.MyUser()).Subscribe();
            await subscribed.Task;

            var claimed = new TaskCompletionSource<string?>();
            conn.Reducers.OnClaimInvite += (ctx, _) => claimed.TrySetResult(Stdb.Failure(ctx));
            conn.Reducers.ClaimInvite(EnrollCode.Text.Trim());
            var failure = await claimed.Task;
            if (failure != null) { Session.Close(conn); ShowError(failure); return; }

            var username = await assigned.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Session.Close(conn);

            var password = EnrollPassword.Password;
            await Task.Run(() => Vault.Save(new VaultContents(username, token, _pendingSecret), password));
            var dialog = new Wpf.Ui.Controls.MessageBox
            {
                Title = "Enrolled",
                Content = $"Enrolled. Your username is {username}. Write it down: the lock screen won't list it.",
                PrimaryButtonText = "OK",
                Owner = this
            };
            await dialog.ShowDialogAsync(true, CancellationToken.None);
            UserBox.Text = username;
            ShowLogin_Click(sender, e);
        }
        catch (Exception ex) { ShowError(ex.Message); }
        finally { EnrollButton.IsEnabled = true; }
    }

    // ---------- Helpers ----------

    bool CheckNewPassword(string a, string b)
    {
        if (a.Length < 12) { ShowError("Use at least 12 characters."); return false; }
        if (a != b) { ShowError("The two passwords don't match."); return false; }
        return true;
    }

    void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    static BitmapImage Qr(string text)
    {
        using var gen = new QRCodeGenerator();
        var png = new PngByteQRCode(gen.CreateQrCode(text, QRCodeGenerator.ECCLevel.Q)).GetGraphic(8);
        var img = new BitmapImage();
        img.BeginInit();
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.StreamSource = new MemoryStream(png);
        img.EndInit();
        return img;
    }
}
