using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;

namespace MediaForge.Services;

public static class ThemeManager
{
    public static void Apply(Window window, bool dark)
    {
        var bg = Brush(dark ? "#111827" : "#F3F4F6");
        var panel = Brush(dark ? "#1F2937" : "#FFFFFF");
        var fg = Brush(dark ? "#F9FAFB" : "#111827");
        var muted = Brush(dark ? "#9CA3AF" : "#4B5563");
        window.Background = bg;
        window.Foreground = fg;

        // Keep theme resources in sync so controls hosted inside DataGrid
        // templates (which are not always reached through the logical tree)
        // can use DynamicResource bindings for their foreground/background.
        if (Application.Current != null)
        {
            Application.Current.Resources["WindowBackgroundBrush"] = bg;
            Application.Current.Resources["WindowForegroundBrush"] = fg;
            Application.Current.Resources["PanelBackgroundBrush"] = panel;
            Application.Current.Resources["MutedForegroundBrush"] = muted;
        }

        ApplyVisual(window, dark, bg, panel, fg, muted);
    }

    private static SolidColorBrush Brush(string value) =>
        new((Color)ColorConverter.ConvertFromString(value));

    private static void ApplyVisual(DependencyObject o, bool dark, Brush bg, Brush panel, Brush fg, Brush muted)
    {
        if (o is Border b && b.Background != null)
            b.Background = panel;

        if (o is TextBlock t) t.Foreground = fg;
        if (o is Label l) l.Foreground = fg;

        if (o is TextBox tb)
        {
            tb.Background = dark ? Brush("#111827") : Brushes.White;
            tb.Foreground = fg;
            tb.BorderBrush = dark ? Brush("#4B5563") : Brush("#D1D5DB");
            tb.CaretBrush = fg;
        }

        if (o is PasswordBox pb)
        {
            pb.Background = dark ? Brush("#111827") : Brushes.White;
            pb.Foreground = fg;
            pb.BorderBrush = dark ? Brush("#4B5563") : Brush("#D1D5DB");
        }

        if (o is ComboBox cb)
            ApplyComboBoxTheme(cb, dark, fg);

        if (o is DataGrid dg)
            ApplyDataGridTheme(dg, dark, fg);

        if (o is Button btn)
        {
            btn.Foreground = fg;
            btn.Background = dark ? Brush("#1F2937") : Brushes.White;
            btn.BorderBrush = dark ? Brush("#4B5563") : Brush("#D1D5DB");
        }

        if (o is CheckBox c) c.Foreground = fg;
        if (o is RadioButton rb) rb.Foreground = fg;

        // These are useful when the container is already materialized.
        if (o is ComboBoxItem cbi)
        {
            cbi.Background = dark ? Brush("#111827") : Brushes.White;
            cbi.Foreground = fg;
        }

        if (o is ListBoxItem lbi)
        {
            lbi.Background = dark ? Brush("#111827") : Brushes.White;
            lbi.Foreground = fg;
        }

        if (o is ItemsControl ic)
        {
            foreach (var item in ic.Items)
                if (item is DependencyObject d)
                    ApplyVisual(d, dark, bg, panel, fg, muted);
        }

        if (o is FrameworkElement fe)
        {
            foreach (var child in LogicalTreeHelper.GetChildren(fe).OfType<DependencyObject>())
                ApplyVisual(child, dark, bg, panel, fg, muted);
        }
    }

    private static void ApplyComboBoxTheme(ComboBox cb, bool dark, Brush fg)
    {
        var comboBg = dark ? Brush("#111827") : Brushes.White;
        var comboBorder = dark ? Brush("#4B5563") : Brush("#D1D5DB");
        var hover = dark ? Brush("#374151") : Brush("#E5E7EB");
        var selected = dark ? Brush("#2563EB") : Brush("#DBEAFE");
        var selectedFg = dark ? Brushes.White : Brush("#111827");

        cb.Background = comboBg;
        cb.Foreground = fg;
        cb.BorderBrush = comboBorder;

        // WPF ComboBox popups use system brushes inside their Popup template.
        // Override those brushes on the ComboBox itself so the popup, its
        // border, and its text do not fall back to the Windows white theme.
        cb.Resources[SystemColors.WindowBrushKey] = comboBg;
        cb.Resources[SystemColors.ControlBrushKey] = comboBg;
        cb.Resources[SystemColors.ControlLightBrushKey] = comboBg;
        cb.Resources[SystemColors.ControlDarkBrushKey] = comboBorder;
        cb.Resources[SystemColors.WindowTextBrushKey] = fg;
        cb.Resources[SystemColors.ControlTextBrushKey] = fg;
        cb.Resources[SystemColors.HighlightBrushKey] = selected;
        cb.Resources[SystemColors.HighlightTextBrushKey] = selectedFg;

        // ComboBox dropdowns are hosted in a Popup and therefore are not part
        // of the normal logical tree. Explicitly style their generated items.
        var itemStyle = new Style(typeof(ComboBoxItem));
        itemStyle.Setters.Add(new Setter(Control.BackgroundProperty, comboBg));
        itemStyle.Setters.Add(new Setter(Control.ForegroundProperty, fg));
        itemStyle.Setters.Add(new Setter(Control.BorderBrushProperty, comboBorder));
        itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(7, 5, 7, 5)));
        itemStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));

        // Do not rely on the Windows default ComboBoxItem template. That template
        // can reintroduce the system white popup/text colors even after the parent
        // ComboBox has been themed. A small explicit template keeps every popup
        // readable in both themes.
        var template = new ControlTemplate(typeof(ComboBoxItem));
        var borderFactory = new FrameworkElementFactory(typeof(Border));
        borderFactory.Name = "ItemBorder";
        borderFactory.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        borderFactory.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        borderFactory.SetValue(Border.BorderThicknessProperty, new Thickness(0));
        borderFactory.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.ContentSourceProperty, "Content");
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        presenter.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
        presenter.SetValue(TextElement.ForegroundProperty, new TemplateBindingExtension(Control.ForegroundProperty));
        borderFactory.AppendChild(presenter);
        template.VisualTree = borderFactory;

        // IMPORTANT: these are Style triggers, not ControlTemplate triggers.
        // Do not use a Setter TargetName here. The item template already binds
        // its Border.Background to the ComboBoxItem.Background property, so
        // changing the item property is enough to update the visual.
        var mouseOver = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        mouseOver.Setters.Add(new Setter(Control.BackgroundProperty, hover));
        mouseOver.Setters.Add(new Setter(Control.ForegroundProperty, fg));
        itemStyle.Triggers.Add(mouseOver);

        var isSelected = new Trigger { Property = Selector.IsSelectedProperty, Value = true };
        isSelected.Setters.Add(new Setter(Control.BackgroundProperty, selected));
        isSelected.Setters.Add(new Setter(Control.ForegroundProperty, selectedFg));
        itemStyle.Triggers.Add(isSelected);
        itemStyle.Setters.Add(new Setter(Control.TemplateProperty, template));

        cb.ItemContainerStyle = itemStyle;

        // Replace the Windows default ComboBox template for non-editable
        // dropdowns. The stock template can ignore the locally themed system
        // brushes and render a light/white closed control with white text.
        // The application resource owns both the closed control and the popup.
        if (!cb.IsEditable && Application.Current?.Resources["ThemeComboBoxTemplate"] is ControlTemplate comboTemplate)
            cb.Template = comboTemplate;

        // Force the popup to use the themed brushes as a resource boundary too.
        cb.Resources[typeof(ComboBoxItem)] = itemStyle;
    }

    private static void ApplyDataGridTheme(DataGrid dg, bool dark, Brush fg)
    {
        var gridBg = dark ? Brush("#111827") : Brushes.White;
        var alternateBg = dark ? Brush("#182131") : Brush("#F9FAFB");
        var gridHeader = dark ? Brush("#1F2937") : Brush("#E5E7EB");
        var gridLines = dark ? Brush("#374151") : Brush("#D1D5DB");
        var selectedRow = dark ? Brush("#2563EB") : Brush("#BFDBFE");
        var selectedCell = dark ? Brush("#1D4ED8") : Brush("#DBEAFE");
        var selectedFg = Brushes.White;

        dg.Background = gridBg;
        dg.Foreground = fg;
        dg.RowBackground = gridBg;
        dg.AlternatingRowBackground = alternateBg;
        dg.HorizontalGridLinesBrush = gridLines;
        dg.VerticalGridLinesBrush = gridLines;

        var headerStyle = new Style(typeof(DataGridColumnHeader));
        headerStyle.Setters.Add(new Setter(Control.BackgroundProperty, gridHeader));
        headerStyle.Setters.Add(new Setter(Control.ForegroundProperty, fg));
        headerStyle.Setters.Add(new Setter(Control.BorderBrushProperty, gridLines));
        headerStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6, 4, 6, 4)));
        dg.ColumnHeaderStyle = headerStyle;

        var cellStyle = new Style(typeof(DataGridCell));
        cellStyle.Setters.Add(new Setter(Control.BackgroundProperty, gridBg));
        cellStyle.Setters.Add(new Setter(Control.ForegroundProperty, fg));
        cellStyle.Setters.Add(new Setter(Control.BorderBrushProperty, Brushes.Transparent));

        // Cell selection must be explicitly styled because the default WPF
        // selection brush can remain white when a custom dark theme is applied.
        var cellSelected = new Trigger { Property = DataGridCell.IsSelectedProperty, Value = true };
        cellSelected.Setters.Add(new Setter(Control.BackgroundProperty, selectedCell));
        cellSelected.Setters.Add(new Setter(Control.ForegroundProperty, selectedFg));
        cellStyle.Triggers.Add(cellSelected);
        dg.CellStyle = cellStyle;

        var rowStyle = new Style(typeof(DataGridRow));
        rowStyle.Setters.Add(new Setter(Control.BackgroundProperty, gridBg));
        rowStyle.Setters.Add(new Setter(Control.ForegroundProperty, fg));
        rowStyle.Setters.Add(new Setter(Control.BorderBrushProperty, Brushes.Transparent));

        var rowSelected = new Trigger { Property = DataGridRow.IsSelectedProperty, Value = true };
        rowSelected.Setters.Add(new Setter(Control.BackgroundProperty, selectedRow));
        rowSelected.Setters.Add(new Setter(Control.ForegroundProperty, selectedFg));
        rowStyle.Triggers.Add(rowSelected);

        var rowHover = new Trigger { Property = DataGridRow.IsMouseOverProperty, Value = true };
        rowHover.Setters.Add(new Setter(Control.BackgroundProperty, dark ? Brush("#374151") : Brush("#E5E7EB")));
        rowStyle.Triggers.Add(rowHover);

        dg.RowStyle = rowStyle;
        dg.Resources[typeof(DataGridRow)] = rowStyle;
        dg.Resources[typeof(DataGridCell)] = cellStyle;
    }
}
