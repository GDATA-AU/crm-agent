using System.Net.Http.Headers;

namespace CrmAgent.Tray;

/// <summary>
/// First-run / reconfigure form. Collects Portal URL, API key, and Azure
/// Storage connection string, validates the portal connection, then saves
/// config and starts the service.
/// </summary>
public sealed class ConnectForm : Form
{
    private readonly TextBox _urlBox;
    private readonly TextBox _apiKeyBox;
    private readonly TextBox _azureBox;
    private readonly Button _testBtn;
    private readonly Button _saveBtn;
    private readonly Label _testStatus;

    public ConnectForm()
    {
        Text = "LGA CRM Agent – Setup";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Width = 540;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        _urlBox = new TextBox { Dock = DockStyle.Fill };
        _apiKeyBox = new TextBox { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
        _azureBox = new TextBox { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
        _testBtn = new Button { Text = "Test Connection", AutoSize = true };
        _saveBtn = new Button { Text = "Save && Start Service", AutoSize = true, Enabled = false };
        _testStatus = new Label { AutoSize = true, Text = string.Empty, Padding = new Padding(4, 6, 0, 0) };

        var layout = new TableLayoutPanel
        {
            ColumnCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        AddRow(layout, 0, "Portal URL:", _urlBox);
        AddRow(layout, 1, "Agent API Key:", _apiKeyBox);
        AddRow(layout, 2, "Azure Storage\nConnection String:", _azureBox);

        // Test row
        var testRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        testRow.Controls.Add(_testBtn);
        testRow.Controls.Add(_testStatus);
        layout.Controls.Add(new Label(), 0, 3);
        layout.Controls.Add(testRow, 1, 3);

        // Separator + Save button
        layout.Controls.Add(new Label { Height = 4 }, 0, 4);
        layout.Controls.Add(new Label { Height = 4 }, 1, 4);
        layout.Controls.Add(new Label(), 0, 5);
        layout.Controls.Add(_saveBtn, 1, 5);
        // Bottom padding row
        layout.Controls.Add(new Label { Height = 8 }, 0, 6);

        Controls.Add(layout);

        // Pre-populate from existing config (reconfigure scenario)
        var existing = ConfigStore.Load();
        if (existing is not null)
        {
            _urlBox.Text = existing.PortalUrl;
            _apiKeyBox.Text = existing.AgentApiKey;
            _azureBox.Text = existing.AzureStorageConnectionString;
        }

        _testBtn.Click += OnTestConnection;
        _saveBtn.Click += OnSave;
    }

    private static void AddRow(TableLayoutPanel layout, int row, string labelText, Control control)
    {
        layout.Controls.Add(new Label
        {
            Text = labelText,
            TextAlign = ContentAlignment.MiddleRight,
            Dock = DockStyle.Fill,
        }, 0, row);
        layout.Controls.Add(control, 1, row);
    }

    private async void OnTestConnection(object? sender, EventArgs e)
    {
        _testBtn.Enabled = false;
        _saveBtn.Enabled = false;
        _testStatus.Text = "Testing…";
        _testStatus.ForeColor = SystemColors.GrayText;

        var url = _urlBox.Text.Trim().TrimEnd('/');
        var key = _apiKeyBox.Text.Trim();

        if (string.IsNullOrEmpty(url) || !Uri.IsWellFormedUriString(url, UriKind.Absolute))
        {
            _testStatus.Text = "✗ Enter a valid URL.";
            _testStatus.ForeColor = Color.Red;
            _testBtn.Enabled = true;
            return;
        }

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
            var response = await http.GetAsync($"{url}/api/agent/jobs");

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                _testStatus.Text = "✗ Invalid API key.";
                _testStatus.ForeColor = Color.Red;
            }
            else
            {
                // 200 (job available), 204 (no jobs) — both confirm auth succeeded
                _testStatus.Text = "✓ Connected.";
                _testStatus.ForeColor = Color.Green;
                _saveBtn.Enabled = true;
            }
        }
        catch (Exception ex)
        {
            _testStatus.Text = $"✗ {ex.Message}";
            _testStatus.ForeColor = Color.Red;
        }
        finally
        {
            _testBtn.Enabled = true;
        }
    }

    private void OnSave(object? sender, EventArgs e)
    {
        try
        {
            ConfigStore.Save(new ConfigStore.AgentSettings(
                _urlBox.Text.Trim().TrimEnd('/'),
                _apiKeyBox.Text.Trim(),
                _azureBox.Text.Trim()));
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to save configuration:\n{ex.Message}",
                "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        try
        {
            ServiceManager.Start();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Configuration saved, but could not start the service:\n{ex.Message}\n\n" +
                "You can start it manually via services.msc.",
                "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        Close();
    }
}
