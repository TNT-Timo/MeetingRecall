using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MeetingRecall.Core.Entities;
using MeetingRecall.Core.Interfaces;
using Windows.UI;

namespace MeetingRecall.Desktop.Controls;

internal sealed class SpeakerProfilesPanel : UserControl
{
    private readonly ISpeakerRepository _repository;
    private readonly Func<Task> _onProfilesChanged;
    private readonly Func<Guid, Task> _playPreview;
    private readonly Func<string, string, string> _translate;
    private readonly StackPanel _rows = new() { Spacing = 10 };
    private readonly Button _deleteAllButton = new();
    private readonly ProgressRing _progress = new()
    {
        Width = 24,
        Height = 24,
        IsActive = true,
        HorizontalAlignment = HorizontalAlignment.Center
    };

    public SpeakerProfilesPanel(
        ISpeakerRepository repository,
        Func<Task> onProfilesChanged,
        Func<Guid, Task> playPreview,
        Func<string, string, string> translate)
    {
        _repository = repository;
        _onProfilesChanged = onProfilesChanged;
        _playPreview = playPreview;
        _translate = translate;
        Content = BuildContent();
        Loaded += SpeakerProfilesPanel_Loaded;
    }

    private FrameworkElement BuildContent()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition
        {
            Height = new GridLength(1, GridUnitType.Star)
        });

        var header = new Grid
        {
            ColumnSpacing = 12,
            Margin = new Thickness(4, 0, 4, 16)
        };
        header.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        header.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = GridLength.Auto
        });
        header.Children.Add(new TextBlock
        {
            Text = _translate(
                "Namen und Farben erkannter Personen verwalten.",
                "Manage names and colors of recognized people."),
            TextWrapping = TextWrapping.Wrap,
            Foreground = ThemeBrush("TextFillColorSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center
        });
        _deleteAllButton.Content = _translate("Alle löschen", "Delete all");
        _deleteAllButton.Visibility = Visibility.Collapsed;
        _deleteAllButton.Click += DeleteAllButton_Click;
        AutomationProperties.SetName(
            _deleteAllButton,
            _translate(
                "Alle Personenprofile löschen",
                "Delete all person profiles"));
        Grid.SetColumn(_deleteAllButton, 1);
        header.Children.Add(_deleteAllButton);
        root.Children.Add(header);

        var scroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _rows
        };
        Grid.SetRow(scroller, 1);
        root.Children.Add(scroller);
        _rows.Children.Add(_progress);
        return root;
    }

    private async void SpeakerProfilesPanel_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= SpeakerProfilesPanel_Loaded;
        await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        _rows.Children.Clear();
        _progress.IsActive = true;
        _deleteAllButton.IsEnabled = false;
        _rows.Children.Add(_progress);

        IReadOnlyList<Speaker> speakers;
        try
        {
            speakers = await _repository.ListAsync();
        }
        catch (Exception exception)
        {
            _progress.IsActive = false;
            _rows.Children.Clear();
            _rows.Children.Add(new TextBlock
            {
                Text = exception.Message,
                TextWrapping = TextWrapping.Wrap,
                Foreground = ThemeBrush("SystemFillColorCriticalBrush")
            });
            return;
        }

        _progress.IsActive = false;
        _rows.Children.Clear();
        _deleteAllButton.IsEnabled = true;
        _deleteAllButton.Visibility = speakers.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (speakers.Count == 0)
        {
            _rows.Children.Add(new TextBlock
            {
                Text = _translate(
                    "Noch wurden keine Personen erkannt.",
                    "No people have been recognized yet."),
                Foreground = ThemeBrush("TextFillColorSecondaryBrush")
            });
            return;
        }

        foreach (Speaker speaker in speakers
                     .OrderByDescending(candidate => candidate.IsNamed)
                     .ThenBy(candidate => candidate.DisplayName, StringComparer.CurrentCultureIgnoreCase))
        {
            _rows.Children.Add(CreateSpeakerRow(speaker));
        }
    }

    private FrameworkElement CreateSpeakerRow(Speaker speaker)
    {
        SolidColorBrush color = CreateColorBrush(speaker.ColorHex);
        var card = new Border
        {
            Padding = new Thickness(16, 13, 14, 13),
            CornerRadius = new CornerRadius(12),
            Background = ThemeBrush("CardBackgroundFillColorDefaultBrush"),
            BorderBrush = ThemeBrush("CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1)
        };

        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        grid.Children.Add(new Border
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(9),
            Background = color,
            VerticalAlignment = VerticalAlignment.Center
        });

        var text = new StackPanel { Spacing = 3 };
        text.Children.Add(new TextBlock
        {
            Text = speaker.DisplayName,
            FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };
        var previewButton = new Button
        {
            Content = new FontIcon { Glyph = "\uE768", FontSize = 14 },
            Tag = speaker,
            Width = 36,
            Height = 32,
            Padding = new Thickness(0)
        };
        ToolTipService.SetToolTip(
            previewButton,
            _translate(
                "Zufällige Sprachpassage abspielen · erneut klicken für eine andere",
                "Play a random voice passage · click again for another"));
        AutomationProperties.SetName(
            previewButton,
            _translate(
                $"Sprachprobe von {speaker.DisplayName} abspielen",
                $"Play a voice sample of {speaker.DisplayName}"));
        previewButton.Click += PreviewButton_Click;
        actions.Children.Add(previewButton);

        var editButton = new Button
        {
            Content = _translate("Bearbeiten", "Edit"),
            Tag = speaker
        };
        AutomationProperties.SetName(
            editButton,
            _translate(
                $"{speaker.DisplayName} bearbeiten",
                $"Edit {speaker.DisplayName}"));
        editButton.Click += EditButton_Click;
        actions.Children.Add(editButton);

        var deleteButton = new Button
        {
            Content = new FontIcon { Glyph = "", FontSize = 14 },
            Tag = speaker
        };
        ToolTipService.SetToolTip(
            deleteButton,
            _translate("Person löschen", "Delete person"));
        AutomationProperties.SetName(
            deleteButton,
            _translate(
                $"{speaker.DisplayName} löschen",
                $"Delete {speaker.DisplayName}"));
        deleteButton.Click += DeleteButton_Click;
        actions.Children.Add(deleteButton);
        Grid.SetColumn(actions, 2);
        grid.Children.Add(actions);

        card.Child = grid;
        return card;
    }

    private async void PreviewButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Speaker speaker } button)
        {
            return;
        }

        button.IsEnabled = false;
        try
        {
            await _playPreview(speaker.Id);
        }
        catch (Exception exception)
        {
            await ShowErrorAsync(exception.Message);
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private async void EditButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Speaker speaker })
        {
            return;
        }

        var nameBox = new TextBox
        {
            Header = _translate("Name", "Name"),
            Text = speaker.IsNamed ? speaker.DisplayName : string.Empty,
            PlaceholderText = speaker.IsNamed ? string.Empty : speaker.DisplayName,
            MaxLength = 120
        };
        var colorBox = new ComboBox
        {
            Header = _translate("Farbe", "Color"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        string[] colorLabels =
        [
            _translate("Blau", "Blue"),
            _translate("Grün", "Green"),
            _translate("Rot", "Red"),
            _translate("Violett", "Violet"),
            _translate("Orange", "Orange"),
            _translate("Türkis", "Turquoise"),
            _translate("Rosa", "Pink"),
            _translate("Indigo", "Indigo"),
            _translate("Oliv", "Olive"),
            _translate("Petrol", "Teal")
        ];

        for (int index = 0; index < SpeakerColorPalette.All.Count; index++)
        {
            string hex = SpeakerColorPalette.All[index];
            var optionContent = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 9
            };
            optionContent.Children.Add(new Border
            {
                Width = 14,
                Height = 14,
                CornerRadius = new CornerRadius(7),
                Background = CreateColorBrush(hex),
                VerticalAlignment = VerticalAlignment.Center
            });
            optionContent.Children.Add(new TextBlock
            {
                Text = colorLabels[index],
                VerticalAlignment = VerticalAlignment.Center
            });

            var option = new ComboBoxItem
            {
                Tag = hex,
                Content = optionContent,
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };
            AutomationProperties.SetName(option, colorLabels[index]);
            colorBox.Items.Add(option);

            if (hex == speaker.ColorHex)
            {
                colorBox.SelectedItem = option;
            }
        }

        var content = new StackPanel { Spacing = 14 };
        content.Children.Add(nameBox);
        content.Children.Add(colorBox);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = _translate("Person bearbeiten", "Edit person"),
            Content = content,
            PrimaryButtonText = _translate("Speichern", "Save"),
            CloseButtonText = _translate("Abbrechen", "Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(nameBox.Text)
        };
        nameBox.TextChanged += (_, _) =>
        {
            dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(nameBox.Text);
        };
        dialog.Opened += (_, _) =>
        {
            nameBox.Focus(FocusState.Programmatic);
            if (speaker.IsNamed)
            {
                nameBox.SelectAll();
            }
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary
            || colorBox.SelectedItem is not ComboBoxItem { Tag: string selectedColorHex })
        {
            return;
        }

        try
        {
            speaker.AssignName(nameBox.Text);
            speaker.SetColor(selectedColorHex);
            await _repository.UpsertAsync(speaker);
            await ReloadAsync();
            await _onProfilesChanged();
        }
        catch (Exception exception)
        {
            await ShowErrorAsync(exception.Message);
        }
    }

    private async void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Speaker speaker })
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = _translate("Person löschen?", "Delete person?"),
            Content = _translate(
                $"„{speaker.DisplayName}“ und der lokale Stimmabdruck werden gelöscht. Vorhandene Transkriptstellen erhalten wieder einen neutralen Namen wie „Person 0002“.",
                $"“{speaker.DisplayName}” and its local voiceprint will be deleted. Existing transcript passages will receive a neutral name such as “Person 0002” again."),
            PrimaryButtonText = _translate("Person löschen", "Delete person"),
            CloseButtonText = _translate("Abbrechen", "Cancel"),
            DefaultButton = ContentDialogButton.Close
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            await _repository.DeleteAsync(speaker.Id);
            await ReloadAsync();
            await _onProfilesChanged();
        }
        catch (Exception exception)
        {
            await ShowErrorAsync(exception.Message);
        }
    }

    private async void DeleteAllButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        IReadOnlyList<Speaker> speakers;
        try
        {
            speakers = await _repository.ListAsync();
        }
        catch (Exception exception)
        {
            await ShowErrorAsync(exception.Message);
            return;
        }

        if (speakers.Count == 0)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = _translate(
                "Alle Personen löschen?",
                "Delete all people?"),
            Content = _translate(
                $"Alle {speakers.Count} Personenprofile, Namen und lokalen Stimmabdrücke werden gelöscht. Bestehende Transkripte bleiben erhalten und verwenden danach neue neutrale Bezeichnungen wie „Person 0001“.",
                $"All {speakers.Count} person profiles, names, and local voiceprints will be deleted. Existing transcripts remain intact and will use new neutral labels such as “Person 0001”."),
            PrimaryButtonText = _translate(
                "Alle endgültig löschen",
                "Delete all permanently"),
            CloseButtonText = _translate("Abbrechen", "Cancel"),
            DefaultButton = ContentDialogButton.Close
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        _deleteAllButton.IsEnabled = false;
        try
        {
            await _repository.DeleteAllAsync();
            await ReloadAsync();
            await _onProfilesChanged();
        }
        catch (Exception exception)
        {
            await ShowErrorAsync(exception.Message);
        }
        finally
        {
            _deleteAllButton.IsEnabled = true;
        }
    }

    private async Task ShowErrorAsync(string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = _translate("Änderung fehlgeschlagen", "Change failed"),
            Content = message,
            CloseButtonText = _translate("Schließen", "Close"),
            DefaultButton = ContentDialogButton.Close
        };
        await dialog.ShowAsync();
    }

    private static SolidColorBrush CreateColorBrush(string colorHex)
    {
        string normalized = SpeakerColorPalette.Normalize(colorHex);
        byte red = byte.Parse(normalized.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        byte green = byte.Parse(normalized.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        byte blue = byte.Parse(normalized.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return new SolidColorBrush(Color.FromArgb(255, red, green, blue));
    }

    private static Brush? ThemeBrush(string key)
    {
        return Application.Current.Resources.TryGetValue(key, out object? value)
            ? value as Brush
            : null;
    }

}
