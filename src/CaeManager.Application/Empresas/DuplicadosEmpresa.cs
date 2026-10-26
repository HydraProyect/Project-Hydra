using System.Resources;
using CaeManager.Domain.Common;
using CaeManager.Domain.Empresas;

namespace CaeManager.Application.Empresas;

/// <summary>
/// Comprobación previa de los dos índices únicos de identidad de Empresa,
/// <c>IX_Empresas_TenantId_Cif</c> e <c>IX_Empresas_TenantId_RazonSocial</c>. Ambos incluyen
/// las Empresas dadas de baja del Tenant propietario: una coincidencia con una baja da un error
/// propio que pide restaurarla, en vez del error genérico de la base.
/// </summary>
public static class DuplicadosEmpresa
{
    private static readonly ResourceManager Textos = new(
        "CaeManager.Application.Empresas.Recursos.ErroresEmpresa", typeof(DuplicadosEmpresa).Assembly);

    public static Error ErrorDuplicado(bool dadaDeBaja, string codigo = "Empresa.CifDuplicado") =>
        Crear(esRazonSocial: false, dadaDeBaja, codigo);

    public static Error ErrorRazonSocialDuplicada(bool dadaDeBaja, string codigo = "Empresa.RazonSocialDuplicada") =>
        Crear(esRazonSocial: true, dadaDeBaja, codigo);

    /// <summary>Error de la importación combinada: nombra el CIF de la fila para que se pueda localizar.</summary>
    public static Error ErrorDeBajaEnImportacion(string cif) =>
        Error.Crear("Empresa.CifDeBaja", string.Format(
            System.Globalization.CultureInfo.CurrentCulture, Textos.GetString("CifDeBajaEnImportacion")!, cif.Trim().ToUpperInvariant()));

    public static async Task<Error?> ComprobarAsync(IEmpresaRepository repositorio, string? cif,
        Guid? excluirId = null, string codigo = "Empresa.CifDuplicado", CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(cif))
            return null;
        var duplicada = await repositorio.ObtenerDuplicadaPorCifAsync(cif, excluirId, cancellationToken);
        return duplicada is null ? null : ErrorDuplicado(duplicada.EstaEliminado, codigo);
    }

    /// <summary>
    /// Lo que rechazaría <c>IX_Empresas_TenantId_RazonSocial</c>: misma razón social recortada, sin
    /// cambiar mayúsculas, bajas incluidas. No sustituye la regla de los comandos de alta y edición,
    /// que además rechazan una Empresa activa con la misma razón social en otras mayúsculas
    /// (<see cref="IEmpresaRepository.ExisteConRazonSocialAsync"/>).
    /// </summary>
    public static async Task<Error?> ComprobarRazonSocialAsync(IEmpresaRepository repositorio, string? razonSocial,
        Guid? excluirId = null, string codigo = "Empresa.RazonSocialDuplicada", CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(razonSocial))
            return null;
        var duplicada = await repositorio.ObtenerDuplicadaPorRazonSocialAsync(razonSocial, excluirId, cancellationToken);
        return duplicada is null ? null : ErrorRazonSocialDuplicada(duplicada.EstaEliminado, codigo);
    }

    private static Error Crear(bool esRazonSocial, bool dadaDeBaja, string codigo) => dadaDeBaja
        ? Error.Crear(esRazonSocial ? "Empresa.RazonSocialDeBaja" : "Empresa.CifDeBaja",
            Textos.GetString(esRazonSocial ? "RazonSocialDeBaja" : "CifDeBaja")!)
        : Error.Crear(codigo, Textos.GetString(esRazonSocial ? "RazonSocialDuplicada" : "CifDuplicado")!);
}
