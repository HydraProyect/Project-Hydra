using CaeManager.Domain.Common;
using CaeManager.Domain.Empresas;

namespace CaeManager.Application.Empresas;

/// <summary>
/// Las comprobaciones de duplicado de <see cref="DuplicadosEmpresa"/> sobre una única lectura de
/// <see cref="IEmpresaRepository.ListarIdentidadesIncluyendoBajasAsync"/>, en vez de una consulta por CIF o por razón
/// social (#1991). Compara igual que esas consultas: el CIF recortado y en mayúsculas, la razón social recortada sin
/// cambiar mayúsculas, ambos contra el valor guardado exacto y con las bajas dentro. Es una foto del momento de la
/// lectura: como las consultas a las que sustituye, no ve lo pendiente de guardar.
/// </summary>
public sealed class CatalogoIdentidadesEmpresa
{
    private readonly ILookup<string, IdentidadEmpresa> _porCif;
    private readonly ILookup<string, IdentidadEmpresa> _porRazonSocial;

    private CatalogoIdentidadesEmpresa(IReadOnlyList<IdentidadEmpresa> identidades)
    {
        _porCif = identidades.Where(i => i.Cif != null).ToLookup(i => i.Cif!, StringComparer.Ordinal);
        _porRazonSocial = identidades.ToLookup(i => i.RazonSocial, StringComparer.Ordinal);
    }

    public static async Task<CatalogoIdentidadesEmpresa> CargarAsync(IEmpresaRepository repositorio, CancellationToken cancellationToken) =>
        new(await repositorio.ListarIdentidadesIncluyendoBajasAsync(cancellationToken));

    /// <summary>Lo que devolvería <see cref="IEmpresaRepository.ObtenerDuplicadaPorCifAsync"/>.</summary>
    public IdentidadEmpresa? DuplicadaPorCif(string cif, Guid? excluirId = null) =>
        _porCif[cif.Trim().ToUpperInvariant()].FirstOrDefault(i => excluirId == null || i.Id != excluirId);

    /// <summary>Lo que devolvería <see cref="DuplicadosEmpresa.ComprobarRazonSocialAsync"/>.</summary>
    public Error? ComprobarRazonSocial(string? razonSocial, Guid? excluirId = null, string codigo = "Empresa.RazonSocialDuplicada")
    {
        if (string.IsNullOrWhiteSpace(razonSocial))
            return null;
        var duplicada = _porRazonSocial[razonSocial.Trim()].FirstOrDefault(i => excluirId == null || i.Id != excluirId);
        return duplicada is null ? null : DuplicadosEmpresa.ErrorRazonSocialDuplicada(duplicada.EstaEliminado, codigo);
    }
}
