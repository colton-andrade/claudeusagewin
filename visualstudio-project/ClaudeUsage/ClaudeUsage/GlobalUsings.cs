// This project enables both UseWPF and UseWindowsForms (the tray icon uses
// WinForms' NotifyIcon). With ImplicitUsings on, that adds a global
// `using System.Drawing`, which makes a bare `Color`/`ColorConverter` ambiguous
// with System.Windows.Media in every WPF file (CS0104). These aliases pin the
// bare names to the WPF (Media) types project-wide so we stop hitting it.
//
// GDI+ tray-icon code that genuinely needs System.Drawing uses the explicit
// `Drawing.` prefix (see App.xaml.cs), so it is unaffected.
global using Color = System.Windows.Media.Color;
global using ColorConverter = System.Windows.Media.ColorConverter;
