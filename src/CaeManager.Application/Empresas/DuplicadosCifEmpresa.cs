using System.Resources;
using CaeManager.Domain.Common;
using CaeManager.Domain.Empresas;

namespace CaeManager.Application.Empresas;

public static class DuplicadosCifEmpresa
{
    private static readonly ResourceManager Textos = new(
        "CaeManager.Application.Empresas.Recursos.ErroresEmpresa", typeof(DuplicadosCifEmpresa).Assembly);

    public static Error ErrorDuplicado(bool dadaDeBaja, string codigo = "Empresa.CifDuplicado") => dadaDeBaja
        ? Error.Crear("Empresa.CifDeBaja", Textos.GetString("CifDeBaja")!)
        : Error.Crear(codigo, Textos.GetString("CifDuplicado")!);

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
}
