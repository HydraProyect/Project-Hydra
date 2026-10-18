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

        // Solo la letra de DNI/NIE lleva aviso propio (la maqueta lo dibuja así). Un CIF con el
        // control mal no corrige nada: la paleta lo trata como «ninguna Empresa con ese CIF».
        string? aviso = null;
        if (!resultado.EsValido && resultado.Tipo is TipoIdentificacion.Dni or TipoIdentificacion.Nie)
            aviso = $"La letra no cuadra: ¿quisiste decir {valor[..^1]}{ValidadorIdentificacion.LetraControlEsperada(valor)}?";
        return new(valor, resultado.Tipo, resultado.EsValido, aviso);
    }
}
