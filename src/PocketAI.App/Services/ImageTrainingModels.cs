using System;
using System.Collections.Generic;
using System.IO;

namespace PocketAI.App.Services;

public enum ImageTrainingModelFamily
{
    Sd15 = 0,
    Sdxl = 1
}

public sealed class ImageTrainingPreset
{
    public ImageTrainingPreset(
        string name,
        ImageTrainingModelFamily family,
        int resolution,
        int rank,
        int alpha,
        int epochs,
        int repeats,
        double learningRate,
        string optimizer,
        string mixedPrecision)
    {
        Name = name;
        Family = family;
        Resolution = resolution;
        Rank = rank;
        Alpha = alpha;
        Epochs = epochs;
        Repeats = repeats;
        LearningRate = learningRate;
        Optimizer = optimizer;
        MixedPrecision = mixedPrecision;
    }

    public string Name { get; set; }
    public ImageTrainingModelFamily Family { get; set; }
    public int Resolution { get; set; }
    public int Rank { get; set; }
    public int Alpha { get; set; }
    public int Epochs { get; set; }
    public int Repeats { get; set; }
    public double LearningRate { get; set; }
    public string Optimizer { get; set; }
    public string MixedPrecision { get; set; }

    public override string ToString() => Name;
}

public sealed record ImageTrainingPrompt(
    int Id,
    string FileName,
    string Prompt,
    string NegativePrompt,
    int Width,
    int Height);

public sealed record ImageTrainingItem(
    string ImagePath,
    string CaptionPath,
    string Caption,
    string Source)
{
    public string Display =>
        $"{Path.GetFileName(ImagePath)} · {Caption}";
}

public sealed class ImageTrainingProject
{
    public string Name { get; set; } = "my-lora";
    public string TriggerWord { get; set; } = "pocketstyle";
    public ImageTrainingModelFamily Family { get; set; } = ImageTrainingModelFamily.Sdxl;
    public string BaseModelPath { get; set; } = string.Empty;
    public string ProjectDirectory { get; set; } = string.Empty;
    public DateTime CreatedLocal { get; set; } = DateTime.Now;
    public List<ImageTrainingPrompt> Prompts { get; set; } = new();
    public List<ImageTrainingItem> Items { get; set; } = new();
}

public sealed record ImageTrainingRunOptions(
    string ProjectDirectory,
    string ProjectName,
    string BaseModelPath,
    ImageTrainingPreset Preset,
    string DatasetConfigPath);
