using System;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;

namespace PenguinExtention.UI
{
    /// <summary>
    /// Visual Studio-themed implicit styles for code-built WPF tool windows. WPF's stock templates for TabItem,
    /// ToggleButton, ComboBox and TreeViewItem hard-code light system colors, so they ignore the dark theme.
    /// The templates below read "P.*" brushes that are copied from the current VS theme (and refreshed on theme change).
    /// </summary>
    internal static class PenguinTheme
    {
        public static void Apply(FrameworkElement root)
        {
            CopyPalette(root);
            root.Resources.MergedDictionaries.Add(Styles());
            void OnThemeChanged(ThemeChangedEventArgs e) => root.Dispatcher.BeginInvoke(new Action(() => CopyPalette(root)));
            VSColorTheme.ThemeChanged += OnThemeChanged;
            root.Unloaded += (s, e) => VSColorTheme.ThemeChanged -= OnThemeChanged;
            root.Loaded += (s, e) => { VSColorTheme.ThemeChanged -= OnThemeChanged; VSColorTheme.ThemeChanged += OnThemeChanged; CopyPalette(root); };
        }

        private static void CopyPalette(FrameworkElement root)
        {
            void Set(string key, object vsKey, Brush fallback)
            {
                root.Resources[key] = (root.TryFindResource(vsKey) as Brush) ?? (Application.Current?.TryFindResource(vsKey) as Brush) ?? fallback;
            }
            Set("P.Bg", EnvironmentColors.ToolWindowBackgroundBrushKey, Brushes.Black);
            Set("P.Text", EnvironmentColors.ToolWindowTextBrushKey, Brushes.White);
            Set("P.Border", EnvironmentColors.ToolWindowBorderBrushKey, Brushes.Gray);
            Set("P.Control", EnvironmentColors.ComboBoxBackgroundBrushKey, Brushes.DimGray);
            Set("P.ControlBorder", EnvironmentColors.ComboBoxBorderBrushKey, Brushes.Gray);
            Set("P.Hover", EnvironmentColors.CommandBarMouseOverBackgroundBeginBrushKey, Brushes.SlateGray);
            Set("P.HoverText", EnvironmentColors.CommandBarTextHoverBrushKey, Brushes.White);
            Set("P.Selected", EnvironmentColors.ToolWindowTabSelectedTabBrushKey, Brushes.SteelBlue);
            Set("P.SelectedText", EnvironmentColors.ToolWindowTabSelectedTextBrushKey, Brushes.White);
            Set("P.Glyph", EnvironmentColors.ComboBoxGlyphBrushKey, Brushes.White);
            Set("P.Popup", EnvironmentColors.ComboBoxPopupBackgroundBeginBrushKey, Brushes.Black);
        }

        private const string Xaml = @"
<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
                    xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>

  <Style TargetType='TabControl'>
    <Setter Property='Background' Value='{DynamicResource P.Bg}'/>
    <Setter Property='BorderBrush' Value='{DynamicResource P.Border}'/>
    <Setter Property='Foreground' Value='{DynamicResource P.Text}'/>
    <Setter Property='Padding' Value='0'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='TabControl'>
          <DockPanel>
            <TabPanel DockPanel.Dock='Top' IsItemsHost='True' Margin='2,2,2,0'/>
            <Border BorderThickness='0,1,0,0' BorderBrush='{TemplateBinding BorderBrush}' Background='{TemplateBinding Background}'>
              <ContentPresenter ContentSource='SelectedContent' Margin='{TemplateBinding Padding}'/>
            </Border>
          </DockPanel>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style TargetType='TabItem'>
    <Setter Property='Foreground' Value='{DynamicResource P.Text}'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='TabItem'>
          <Border x:Name='Bd' Background='Transparent' Padding='9,3,9,3' Margin='0,0,2,0' Cursor='Hand'>
            <ContentPresenter ContentSource='Header' TextElement.Foreground='{TemplateBinding Foreground}'/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'>
              <Setter TargetName='Bd' Property='Background' Value='{DynamicResource P.Hover}'/>
              <Setter Property='Foreground' Value='{DynamicResource P.HoverText}'/>
            </Trigger>
            <Trigger Property='IsSelected' Value='True'>
              <Setter TargetName='Bd' Property='Background' Value='{DynamicResource P.Selected}'/>
              <Setter Property='Foreground' Value='{DynamicResource P.SelectedText}'/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style TargetType='ToggleButton'>
    <Setter Property='Foreground' Value='{DynamicResource P.Text}'/>
    <Setter Property='Background' Value='{DynamicResource P.Control}'/>
    <Setter Property='BorderBrush' Value='{DynamicResource P.ControlBorder}'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='ToggleButton'>
          <Border x:Name='Bd' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}'
                  BorderThickness='1' Padding='{TemplateBinding Padding}' Cursor='Hand'>
            <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center' TextElement.Foreground='{TemplateBinding Foreground}'/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsChecked' Value='False'>
              <Setter TargetName='Bd' Property='Opacity' Value='0.55'/>
            </Trigger>
            <Trigger Property='IsChecked' Value='True'>
              <Setter TargetName='Bd' Property='Background' Value='{DynamicResource P.Selected}'/>
              <Setter Property='Foreground' Value='{DynamicResource P.SelectedText}'/>
            </Trigger>
            <Trigger Property='IsMouseOver' Value='True'>
              <Setter TargetName='Bd' Property='Opacity' Value='1'/>
              <Setter TargetName='Bd' Property='BorderBrush' Value='{DynamicResource P.Selected}'/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style TargetType='ComboBox'>
    <Setter Property='Foreground' Value='{DynamicResource P.Text}'/>
    <Setter Property='Background' Value='{DynamicResource P.Control}'/>
    <Setter Property='BorderBrush' Value='{DynamicResource P.ControlBorder}'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='ComboBox'>
          <Grid>
            <ToggleButton Focusable='False' ClickMode='Press'
                          IsChecked='{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}'>
              <ToggleButton.Template>
                <ControlTemplate TargetType='ToggleButton'>
                  <Border x:Name='Bd' Background='{DynamicResource P.Control}' BorderBrush='{DynamicResource P.ControlBorder}' BorderThickness='1'>
                    <Path HorizontalAlignment='Right' VerticalAlignment='Center' Margin='0,0,7,0' Data='M 0 0 L 4 4 L 8 0 Z' Fill='{DynamicResource P.Glyph}'/>
                  </Border>
                  <ControlTemplate.Triggers>
                    <Trigger Property='IsMouseOver' Value='True'>
                      <Setter TargetName='Bd' Property='BorderBrush' Value='{DynamicResource P.Selected}'/>
                    </Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
              </ToggleButton.Template>
            </ToggleButton>
            <ContentPresenter IsHitTestVisible='False' Margin='6,3,22,3' VerticalAlignment='Center'
                              Content='{TemplateBinding SelectionBoxItem}' ContentTemplate='{TemplateBinding SelectionBoxItemTemplate}'
                              TextElement.Foreground='{TemplateBinding Foreground}'/>
            <Popup IsOpen='{TemplateBinding IsDropDownOpen}' Placement='Bottom' AllowsTransparency='True' Focusable='False' PopupAnimation='None'>
              <Border Background='{DynamicResource P.Popup}' BorderBrush='{DynamicResource P.ControlBorder}' BorderThickness='1'
                      MinWidth='{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}' MaxHeight='300'>
                <ScrollViewer><ItemsPresenter/></ScrollViewer>
              </Border>
            </Popup>
          </Grid>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style TargetType='ComboBoxItem'>
    <Setter Property='Foreground' Value='{DynamicResource P.Text}'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='ComboBoxItem'>
          <Border x:Name='Bd' Background='Transparent' Padding='6,3'>
            <ContentPresenter TextElement.Foreground='{TemplateBinding Foreground}'/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsHighlighted' Value='True'>
              <Setter TargetName='Bd' Property='Background' Value='{DynamicResource P.Selected}'/>
              <Setter Property='Foreground' Value='{DynamicResource P.SelectedText}'/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style TargetType='TreeViewItem'>
    <Setter Property='Foreground' Value='{DynamicResource P.Text}'/>
  </Style>

  <Style TargetType='TextBox'>
    <Setter Property='Foreground' Value='{DynamicResource P.Text}'/>
    <Setter Property='Background' Value='{DynamicResource P.Control}'/>
    <Setter Property='BorderBrush' Value='{DynamicResource P.ControlBorder}'/>
    <Setter Property='CaretBrush' Value='{DynamicResource P.Text}'/>
  </Style>
</ResourceDictionary>";

        private static ResourceDictionary _styles;

        private static ResourceDictionary Styles()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return _styles ?? (_styles = (ResourceDictionary)XamlReader.Parse(Xaml));
        }
    }
}
