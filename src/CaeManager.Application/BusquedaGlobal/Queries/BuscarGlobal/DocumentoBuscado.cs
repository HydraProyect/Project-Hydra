using CaeManager.Domain.Common;

namespace CaeManager.Application.BusquedaGlobal.Queries.BuscarGlobal;

/// <summary>Documento completo buscado por coincidencia exacta, nunca por fragmentos.</summary>
public sealed record DocumentoBuscado(string Valor, TipoIdentificacion Tipo, bool EsValido, string? Aviso)
{
    public static DocumentoBuscado? Analizar(string termino)
    {
        var valor = ValidadorIdentificacion.Normalizar(termino);
        if (valor is null) return null;
        var resultado = ValidadorIdentificacion.Analizar(valor);
        if (resultado.Tipo is not (TipoIdentificacion.Dni or TipoIdentificacion.Nie or TipoIdentificacion.NifEmpresa))
            return null;

        string? aviso = null;
        if (!resultado.EsValido)
        {
            aviso = resultado.Tipo is TipoIdentificacion.Dni or TipoIdentificacion.Nie
                ? $"La letra no cuadra: ¿quisiste decir {valor[..^1]}{ValidadorIdentificacion.LetraControlEsperada(valor)}?"
                : "El dígito de control del CIF no es válido.";
        }
        return new(valor, resultado.Tipo, resultado.EsValido, aviso);
    }
}
