namespace SqlBackupN.Core.Domain;

/// <summary>Apariencia de la aplicación.</summary>
public enum AppTheme
{
    /// <summary>Sigue el modo claro u oscuro de Windows.</summary>
    System = 0,

    /// <summary>Siempre claro.</summary>
    Light = 1,

    /// <summary>Siempre oscuro.</summary>
    Dark = 2
}

/// <summary>Preferencias de la persona que usa la interfaz (no son parte de los jobs ni se comparten con el servicio).</summary>
public sealed class UserSettings
{
    /// <summary>Modo claro, oscuro o el de Windows.</summary>
    public AppTheme Theme { get; set; } = AppTheme.System;
}
