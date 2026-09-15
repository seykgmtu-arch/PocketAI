using System;
using System.Collections.Generic;
using System.IO;


namespace PocketAI.App.Services;

public enum ImageTrainingModelFamily
{
    Sd15 = 0,
    Sdxl = 1
}

public sealed record ImageTrainingPreset(
    string Name,
    ImageTrainingModelFamily Family,
    int Resolution,
    int Rank,
    int Alpha,
    int Epochs,
    int Repeats,
    double LearningRate,
    string Optimizer,
    string MixedPrecision)
{
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
