using CaeManager.Domain.Common;

namespace CaeManager.Domain.Empresas;

/// <summary>
/// El usuario y la contraseña con los que una Empresa entra en la Plataforma CAE de un
/// Cliente empresarial (<see cref="PlataformaCaeClienteEmpresarial"/>). Una fila como
/// mucho por (Tenant propietario, Cliente empresarial, Empresa). Caja «Plataforma CAE»
/// de Cliente 360, bloque «Accesos de cada Empresa» (maqueta del 17-10).
///
/// <para>
/// Decisión del propietario (2026-10-18): las credenciales de plataformas externas se
/// guardan dentro de TALVEG, cifradas, y mostrar, copiar y editar quedan en la
/// auditoría. Pertenecen al Tenant propietario del Cliente 360.
/// </para>
///
/// <para>
/// Dato sensible: <see cref="Usuario"/> y <see cref="Contrasena"/> se cifran en reposo
/// con un <c>IDataProtector</c> propio mediante un ValueConverter de EF Core
/// (<c>CaeManagerDbContext</c>); este tipo de dominio no conoce el cifrado. La
/// auditoría de cambios los enmascara (<c>AuditoriaInterceptor</c>). La contraseña
/// anterior no se conserva: cambiarla la sustituye.
/// </para>
/// </summary>
public class AccesoPlataformaCaeEmpresa : EntidadConTenant
{
    public const int LongitudMaximaUsuario = 200;
    public const int LongitudMaximaContrasena = 500;

    public Guid ClienteEmpresarialId { get; private set; }
    public Guid EmpresaId { get; private set; }
    public string? Usuario { get; private set; }
    public string? Contrasena { get; private set; }

    /// <summary>Cuándo se fijó por última vez la contraseña; null si nunca se ha guardado una.</summary>
    public DateTime? ContrasenaCambiadaEnUtc { get; private set; }

    /// <summary>Quién (cuenta real) fijó por última vez la contraseña.</summary>
    public Guid? ContrasenaCambiadaPorUsuarioId { get; private set; }

    private AccesoPlataformaCaeEmpresa()
    {
    }

    public AccesoPlataformaCaeEmpresa(Guid clienteEmpresarialId, Guid empresaId)
    {
        if (clienteEmpresarialId == Guid.Empty)
            throw new ArgumentException("El acceso debe ser de la plataforma de un Cliente.", nameof(clienteEmpresarialId));
        if (empresaId == Guid.Empty)
            throw new ArgumentException("El acceso debe pertenecer a una Empresa.", nameof(empresaId));
        if (clienteEmpresarialId == empresaId)
            throw new ArgumentException("Una Empresa no puede ser su propio Cliente.", nameof(empresaId));

        ClienteEmpresarialId = clienteEmpresarialId;
        EmpresaId = empresaId;
    }

    public void CambiarUsuario(string? usuario)
    {
        var limpio = string.IsNullOrWhiteSpace(usuario) ? null : usuario.Trim();
        if (limpio?.Length > LongitudMaximaUsuario)
            throw new ArgumentException($"El usuario no puede superar {LongitudMaximaUsuario} caracteres.", nameof(usuario));

        Usuario = limpio;
    }

    /// <summary>
    /// Sustituye la contraseña. Vacía no es «bórrala»: quien llama no la invoca (la
    /// edición con el campo vacío conserva la actual, igual que DEC-62).
    /// </summary>
    public void CambiarContrasena(string contrasena, Guid? cambiadaPorUsuarioId, DateTime ahoraUtc)
    {
        if (string.IsNullOrEmpty(contrasena))
            throw new ArgumentException("La contraseña nueva no puede estar vacía.", nameof(contrasena));
        if (contrasena.Length > LongitudMaximaContrasena)
            throw new ArgumentException($"La contraseña no puede superar {LongitudMaximaContrasena} caracteres.", nameof(contrasena));

        Contrasena = contrasena;
        ContrasenaCambiadaEnUtc = ahoraUtc;
        ContrasenaCambiadaPorUsuarioId = cambiadaPorUsuarioId;
    }
}
