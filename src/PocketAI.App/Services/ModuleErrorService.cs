using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PocketAI.App.Services;

public static class ModuleErrorService
{
    public static FrameworkElement BuildErrorPanel(
        string moduleTitle,
        string logFileName,
        Exception exception)
    {
        var root =
            exception.GetBaseException();

        var message =
            root.GetType().FullName +
            Environment.NewLine +
            root.Message;

        return new Border
        {
            Background =
                new SolidColorBrush(
                    Color.FromRgb(
                        58,
                        29,
                        29)),
            BorderBrush =
                new SolidColorBrush(
                    Color.FromRgb(
                        127,
                        29,
                        29)),
            BorderThickness =
                new Thickness(1),
            CornerRadius =
                new CornerRadius(10),
            Padding =
                new Thickness(14),
            Child =
                new StackPanel
                {
                    Children =
                    {
                        new TextBlock
                        {
                            Text =
                                moduleTitle +
                                " остановлен из-за ошибки. PocketAI продолжает работать.",
                            FontWeight =
                                FontWeights.SemiBold,
                            Foreground =
                                new SolidColorBrush(
                                    Color.FromRgb(
                                        254,
                                        202,
                                        202)),
                            TextWrapping =
                                TextWrapping.Wrap
                        },
                        new TextBlock
                        {
                            Text =
                                message,
                            Margin =
                                new Thickness(
                                    0,
                                    10,
                                    0,
                                    0),
                            Foreground =
                                new SolidColorBrush(
                                    Color.FromRgb(
                                        254,
                                        226,
                                        226)),
                            TextWrapping =
                                TextWrapping.Wrap
                        },
                        new TextBlock
                        {
                            Text =
                                "Подробности: logs\\" +
                                logFileName,
                            Margin =
                                new Thickness(
                                    0,
                                    10,
                                    0,
                                    0),
                            Foreground =
                                new SolidColorBrush(
                                    Color.FromRgb(
                                        148,
                                        163,
                                        184)),
                            TextWrapping =
                                TextWrapping.Wrap
                        }
                    }
                }
        };
    }

    public static void WriteException(
        string logFileName,
        Exception exception)
    {
        try
        {
            var directory =
                Path.Combine(
                    AppContext.BaseDirectory,
                    "logs");

            Directory.CreateDirectory(
                directory);

            var path =
                Path.Combine(
                    directory,
                    logFileName);

            File.AppendAllText(
                path,
                $"[{DateTime.Now:O}]{Environment.NewLine}" +
                exception +
                Environment.NewLine +
                new string(
                    '-',
                    72) +
                Environment.NewLine);
        }
        catch
        {
        }
    }

    public static void WriteText(
        string logFileName,
        string text)
    {
        try
        {
            var directory =
                Path.Combine(
                    AppContext.BaseDirectory,
                    "logs");

            Directory.CreateDirectory(
                directory);

            File.AppendAllText(
                Path.Combine(
                    directory,
                    logFileName),
                $"[{DateTime.Now:O}] {text}{Environment.NewLine}");
        }
        catch
        {
        }
    }
}
