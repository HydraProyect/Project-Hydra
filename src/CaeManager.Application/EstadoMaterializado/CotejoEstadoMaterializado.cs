using CaeManager.Domain.Documentos;
using CaeManager.Domain.EstadoMaterializado;

namespace CaeManager.Application.EstadoMaterializado;

/// <summary>
/// Comparación del cotejo nocturno (diseño § 7.2): un lote del recálculo completo frente a lo guardado en su mismo alcance
/// (el del escritor, <see cref="CalculoEstadoMaterializado"/>). Compara las columnas calculadas; <c>DiaCalculo</c> y
/// <c>CalculadoEnUtc</c> no cuentan (una fila igual calculada otro día no es un defecto), y una fila escrita con otra
/// versión de la regla cuenta como distinta. Cada diferencia da la raíz que la rehace, por encima, nunca por debajo.
/// </summary>
public static class CotejoEstadoMaterializado
{
    /// <summary>Una fila guardada con la versión de la regla que la escribió.</summary>
    public readonly record struct Guardada<TFila>(TFila Fila, int VersionCalculo);

    /// <summary>Compara un lote y suma sus diferencias y sus raíces en <paramref name="diferencias"/>.</summary>
    public static void CompararLote(
        CalculoEstadoMaterializado calculo,
        IEnumerable<Guardada<FilaEstadoRequisito>> pares,
        IEnumerable<Guardada<FilaEstadoEnPlataforma>> plataforma,
        IEnumerable<Guardada<FilaEstadoEntidad>> entidades,
        DiferenciasCotejo diferencias)
    {
        ArgumentNullException.ThrowIfNull(calculo);
        ArgumentNullException.ThrowIfNull(diferencias);

        Comparar(calculo.Pares, pares, p => (p.CentroId, p.AmbitoTitular, p.TitularId, p.TipoDocumentoId),
            diferencias.Pares, diferencias.AnotarRaizDePar);
        Comparar(calculo.Plataforma, plataforma, p => (p.CentroId, p.CanalGestionDocumentalId, p.AmbitoTitular, p.TitularId, p.TipoDocumentoId),
            diferencias.Plataforma, p => diferencias.AnotarRaiz(AmbitoEstadoEntidad.Centro, p.CentroId));
        Comparar(calculo.Entidades, entidades, e => (e.Ambito, e.EntidadId),
            diferencias.Entidades, e => diferencias.AnotarRaiz(e.Ambito, e.EntidadId));
    }

    private static void Comparar<TFila, TClave>(
        IEnumerable<TFila> calculadas, IEnumerable<Guardada<TFila>> guardadas, Func<TFila, TClave> clave,
        RecuentoCotejo recuento, Action<TFila> raiz)
        where TClave : notnull
    {
        var porClave = new Dictionary<TClave, TFila>();
        foreach (var fila in calculadas)
            porClave.TryAdd(clave(fila), fila);

        var vistas = new HashSet<TClave>();
        foreach (var guardada in guardadas)
        {
            var k = clave(guardada.Fila);
            if (!vistas.Add(k))
            {
                // Dos filas con la misma clave natural: el índice único lo impide; si pasara, es un defecto.
                recuento.Distintas++;
                raiz(guardada.Fila);
            }
            else if (!porClave.TryGetValue(k, out var calculada))
            {
                recuento.Sobran++;
                raiz(guardada.Fila);
            }
            else if (guardada.VersionCalculo != CalculoEstadoMaterializado.VersionDeLaRegla || !EqualityComparer<TFila>.Default.Equals(calculada, guardada.Fila))
            {
                recuento.Distintas++;
                raiz(calculada);
            }
        }

        foreach (var (k, calculada) in porClave)
        {
            if (vistas.Contains(k))
                continue;
            recuento.Faltan++;
            raiz(calculada);
        }
    }
}

/// <summary>Filas que faltan (el recálculo las trae y no están), sobran (están y el recálculo no las trae) o son distintas.</summary>
public sealed class RecuentoCotejo
{
    public int Faltan { get; set; }
    public int Sobran { get; set; }
    public int Distintas { get; set; }
    public int Total => Faltan + Sobran + Distintas;

    public override string ToString() => $"{Faltan} faltan, {Sobran} sobran, {Distintas} distintas";
}

/// <summary>
/// Lo que el cotejo de un Tenant encontró: recuentos por tabla (sin identificadores de personas, para la alerta) y las raíces
/// que lo corrigen. Las filas de entidades que ya no existen (<see cref="Huerfanas"/>) piden el Tenant entero, que las purga.
/// </summary>
public sealed class DiferenciasCotejo
{
    private readonly HashSet<Guid> _centros = [];
    private readonly HashSet<Guid> _trabajadores = [];
    private readonly HashSet<Guid> _empresas = [];

    public RecuentoCotejo Pares { get; } = new();
    public RecuentoCotejo Plataforma { get; } = new();
    public RecuentoCotejo Entidades { get; } = new();

    /// <summary>Filas de Centros, Trabajadores o Empresas que ya no existen.</summary>
    public int Huerfanas { get; set; }

    public int Total => Pares.Total + Plataforma.Total + Entidades.Total + Huerfanas;

    public RaicesDeRecalculo Raices => new(_centros.ToList(), _trabajadores.ToList(), _empresas.ToList());

    /// <summary>Un par lo rehace su titular (sus pares en todos sus Centros); uno de otro ámbito, su Centro entero.</summary>
    public void AnotarRaizDePar(FilaEstadoRequisito par)
    {
        ArgumentNullException.ThrowIfNull(par);
        switch (par.AmbitoTitular)
        {
            case AmbitoAplicacion.Trabajador:
                _trabajadores.Add(par.TitularId);
                break;
            case AmbitoAplicacion.Empresa:
                _empresas.Add(par.TitularId);
                break;
            default:
                _centros.Add(par.CentroId);
                break;
        }
    }

    /// <summary>La raíz de una fila de entidad; una Subcontrata se rehace como la Empresa (la clausura las trata igual).</summary>
    public void AnotarRaiz(AmbitoEstadoEntidad ambito, Guid id)
    {
        switch (ambito)
        {
            case AmbitoEstadoEntidad.Centro:
                _centros.Add(id);
                break;
            case AmbitoEstadoEntidad.Trabajador:
                _trabajadores.Add(id);
                break;
            default:
                _empresas.Add(id);
                break;
        }
    }

    public override string ToString() =>
        $"pares: {Pares}; Plataforma CAE: {Plataforma}; entidades: {Entidades}; {Huerfanas} de entidades que ya no existen";
}
