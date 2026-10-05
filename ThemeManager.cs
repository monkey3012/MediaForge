using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
        cb.Resources[SystemColors.ActiveBorderBrushKey] = comboBorder;
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

        var mouseOver = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        mouseOver.Setters.Add(new Setter(Control.BackgroundProperty, hover));
        mouseOver.Setters.Add(new Setter(Control.ForegroundProperty, fg));
        itemStyle.Triggers.Add(mouseOver);

        var isSelected = new Trigger { Property = Selector.IsSelectedProperty, Value = true };
        isSelected.Setters.Add(new Setter(Control.BackgroundProperty, selected));
        isSelected.Setters.Add(new Setter(Control.ForegroundProperty, selectedFg));
        itemStyle.Triggers.Add(isSelected);

        cb.ItemContainerStyle = itemStyle;

        // Make the currently selected value readable in the closed ComboBox.
        var selectedItemStyle = new Style(typeof(ComboBoxItem));
        selectedItemStyle.Setters.Add(new Setter(Control.BackgroundProperty, comboBg));
        selectedItemStyle.Setters.Add(new Setter(Control.ForegroundProperty, fg));
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
