using System;
using System.IO;
using SoftProof.Core.Models;

namespace SoftProof.Core.Utilities;

/// <summary>
/// Finds the ICC profiles a soft proof needs. The job may name them; when it does not, the
/// Windows colour directory supplies sRGB for the display and SWOP for the press — but an
/// explicitly named profile that is missing is an error, not something to silently replace.
/// </summary>
public static class ProofProfileLocator
{
    public const string DefaultDisplayProfile = "sRGB Color Space Profile.icm";
    public const string DefaultSimulationProfile = "USWebCoatedSWOP.icc";

    public static string SystemProfileDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                     "System32", "spool", "drivers", "color");

    public static (string Display, string Simulation) Resolve(ProofSettings? settings, string? profileDirectory = null)
    {
        var directory = profileDirectory ?? SystemProfileDirectory;

        var display = Pick(settings?.DisplayProfilePath, DefaultDisplayProfile, directory, "display");
        var simulation = Pick(settings?.SimulationProfilePath, DefaultSimulationProfile, directory, "simulation");

        return (display, simulation);
    }

    private static string Pick(string? configured, string fallbackName, string directory, string role)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (File.Exists(configured))
                return configured;

            throw new FileNotFoundException(
                $"The {role} profile named by the job does not exist: {configured}. " +
                "Pick another profile in Preferences, or clear the setting to use the system default.",
                configured);
        }

        var candidate = Path.Combine(directory, fallbackName);

        if (File.Exists(candidate))
            return candidate;

        throw new FileNotFoundException(
            $"No {role} profile is available: '{fallbackName}' is not in {directory}. " +
            "Install an ICC profile or set one in Preferences.",
            candidate);
    }
}
