using CaeManager.Application.Exportacion;
using Microsoft.AspNetCore.DataProtection;

namespace CaeManager.Infrastructure.Exportacion;

/// <summary>
/// <see cref="IProtectorContrasenaExportacion"/> con ASP.NET Data Protection y un propósito propio: una contraseña
/// protegida aquí no la desprotege ningún otro protector de la aplicación, ni este desprotege lo de otros.
/// </summary>
public class ProtectorContrasenaExportacion(IDataProtectionProvider proveedor) : IProtectorContrasenaExportacion
{
    public const string Proposito = "CaeManager.ExportacionCompleta.Contrasena.v1";

    private readonly IDataProtector _protector = proveedor.CreateProtector(Proposito);

    public string Proteger(string contrasena) => _protector.Protect(contrasena);

    public string Desproteger(string contrasenaProtegida) => _protector.Unprotect(contrasenaProtegida);
}
