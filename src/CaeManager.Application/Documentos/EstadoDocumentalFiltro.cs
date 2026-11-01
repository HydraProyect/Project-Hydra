using CaeManager.Domain.Documentos;

namespace CaeManager.Application.Documentos;

/// <summary>
/// Vocabulario del filtro de "estado documental" que usan los listados de
/// Trabajador, Empresa y Vehículo — entidades sin estado propio en el modelo,
/// cuyo estado se deriva de sus Documentos (ver
/// <see cref="ICalculoEstadoDocumentalService"/>).
///
/// Vive aquí y no en cada Query para que las tres pantallas admitan
/// exactamente los mismos valores: son la misma pregunta hecha sobre tres
/// tablas distintas.
/// </summary>
public static class EstadoDocumentalFiltro
{
    /// <summary>No es un estado de vigencia: el propietario no tiene ningún Documento.</summary>
    public const string SinDocumentos = "SinDocumentos";

    /// <summary>
    /// «Al corriente» — <b>no es un estado documental, es una ausencia</b>: el
    /// Cliente no tiene ninguna alerta abierta.
    ///
    /// <para>
    /// Viaja por el hilo como <c>"Vigente"</c> porque
    /// <see cref="ObtenerClientes.ObtenerClientesQuery"/> **secuestra** ese
    /// valor como centinela: <c>Vigente</c> nunca aparece en los estados
    /// presentes de un Cliente (las alertas no se emiten para lo que está en
    /// regla), así que servía para pedir «sin ninguna alerta» sin añadir un
    /// parámetro más. La constante existe para que ese secuestro esté
    /// <b>nombrado en los dos extremos</b> en vez de deducirse leyendo la
    /// consulta.
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b>Deuda declarada</b>: sigue siendo un centinela. El día que las
    /// alertas emitan <c>Vigente</c>, este filtro dejará de significar lo que
    /// dice y no habrá nada que avise. El arreglo de verdad es un valor de
    /// filtro propio —como <see cref="SinDocumentos"/>—, y cuesta romper los
    /// filtros guardados y los enlaces que hoy llevan <c>?estado=Vigente</c>.
    /// </para>
    /// </summary>
    public const string AlCorriente = nameof(Domain.Documentos.EstadoDocumento.Vigente);

    /// <summary>Separador de varios estados en un mismo filtro: <c>"Vencido,Urgente,Proximo"</c>.</summary>
    public const char Separador = ',';

    /// <summary>
    /// Los valores de un filtro, que puede traer varios estados separados por <see cref="Separador"/> (la franja
    /// de estado de los listados deja marcar más de uno). Un filtro de un solo estado, como los de los enlaces y
    /// filtros guardados anteriores a la franja, es una lista de un elemento.
    /// </summary>
    public static IReadOnlyList<string> Separar(string? filtro) =>
        string.IsNullOrWhiteSpace(filtro)
            ? []
            : filtro.Split(Separador, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.Ordinal)
                .ToList();

    /// <summary>
    /// <paramref name="estado"/> es null cuando el propietario no tiene
    /// Documentos. Un filtro vacío o desconocido no descarta nada, igual que
    /// un <c>OrdenarPor</c> desconocido cae al orden por defecto. Con varios
    /// valores coincide el que cumpla cualquiera; los desconocidos se ignoran,
    /// y solo si TODOS lo son el filtro no descarta nada.
    /// </summary>
    public static bool Coincide(EstadoDocumento? estado, string? filtro)
    {
        var algunoConocido = false;
        foreach (var valor in Separar(filtro))
        {
            if (valor == SinDocumentos)
            {
                algunoConocido = true;
                if (estado is null)
                    return true;
            }
            else if (EsNombreDeEstado(valor, out var esperado))
            {
                algunoConocido = true;
                if (estado == esperado)
                    return true;
            }
        }

        return !algunoConocido;
    }

    /// <summary>
    /// El filtro traducido a las <see cref="ClaveOrden"/> que deja pasar, para los listados que filtran en SQL por
    /// la misma clave con la que ordenan. <c>null</c>: el filtro no descarta nada (vacío, o ningún valor conocido).
    /// Lista vacía: ningún propietario coincide — es lo que ocurre con <see cref="SinDocumentos"/> (el SQL de esos
    /// listados no distingue «sin documentos» de <see cref="EstadoDocumento.SinCaducidad"/>) y con los estados que
    /// un propietario nunca tiene (<see cref="EstadoDocumento.EnTolerancia"/>): un filtro válido pero no aplicable
    /// devuelve nada, no todo. Con <paramref name="incluirFaltantes"/>, admite también Faltante y traduce todos los
    /// estados con <see cref="ClaveOrdenConFaltantes"/>; sin pedirlo, Faltante no devuelve filas.
    /// </summary>
    public static IReadOnlyList<int>? ClavesDeOrden(string? filtro, bool incluirFaltantes = false)
    {
        var algunoConocido = false;
        var claves = new List<int>();
        foreach (var valor in Separar(filtro))
        {
            if (valor == SinDocumentos)
            {
                algunoConocido = true;
            }
            else if (EsNombreDeEstado(valor, out var estado))
            {
                algunoConocido = true;
                var clave = incluirFaltantes ? ClaveOrdenConFaltantes(estado) : ClaveOrden(estado);
                if ((ClaveOrden(estado) < ClaveSinEstadoDePropietario || incluirFaltantes && estado == EstadoDocumento.Faltante)
                    && !claves.Contains(clave))
                    claves.Add(clave);
                // «Vencido» deja pasar también el vencido que solo avisa: se rotula igual.
                if (incluirFaltantes && estado == EstadoDocumento.Vencido && !claves.Contains(ClaveVencidoSinBloqueo))
                    claves.Add(ClaveVencidoSinBloqueo);
            }
        }

        return algunoConocido ? claves : null;
    }

    /// <summary>
    /// Recuentos por <see cref="ClaveOrden"/> (lo que devuelve un <c>GROUP BY</c> sobre la clave) convertidos al
    /// diccionario por nombre de estado de <c>ResultadoPaginado.RecuentosPorEstado</c>. Lleva todos los estados
    /// que un propietario puede tener, también los que no tienen filas (0), para que «no hay ninguno» no se
    /// confunda con «no se contó». Con <paramref name="incluirFaltantes"/> usa <see cref="ClaveOrdenConFaltantes"/>
    /// e incluye Faltante; los demás listados conservan sus claves y estados habituales.
    /// </summary>
    public static IReadOnlyDictionary<string, int> RecuentosPorEstado(IReadOnlyDictionary<int, int> filasPorClave, bool incluirFaltantes = false) =>
        Enum.GetValues<EstadoDocumento>()
            .Where(estado => ClaveOrden(estado) < ClaveSinEstadoDePropietario || incluirFaltantes && estado == EstadoDocumento.Faltante)
            .ToDictionary(estado => estado.ToString(), estado => filasPorClave.GetValueOrDefault(
                incluirFaltantes ? ClaveOrdenConFaltantes(estado) : ClaveOrden(estado))
                // Con requisitos obligatorios, «Vencido» cuenta también el vencido que solo avisa (el mismo que deja pasar el filtro).
                + (incluirFaltantes && estado == EstadoDocumento.Vencido ? filasPorClave.GetValueOrDefault(ClaveVencidoSinBloqueo) : 0));

    /// <summary>
    /// Orden de los propietarios con requisitos obligatorios de Centro (listados de Empresas y de Trabajadores): primero lo
    /// que va en rojo, Vencido por un requisito obligatorio y Faltante; después el vencido que solo avisa
    /// (<see cref="ClaveVencidoSinBloqueo"/>) y los demás estados en su orden habitual. Aquí <see cref="EstadoDocumento.Vencido"/>
    /// es el que va en rojo. Los otros listados usan <see cref="ClaveOrden"/>.
    /// </summary>
    public static int ClaveOrdenConFaltantes(EstadoDocumento? estado) =>
        estado == EstadoDocumento.Vencido ? 0
        : estado == EstadoDocumento.Faltante ? 1
        : ClaveOrden(estado) + 2;

    /// <summary>
    /// La clave de <see cref="ClaveOrdenConFaltantes"/> del vencido que ningún Centro visible marca obligatorio: se rotula
    /// «Vencido» en el tono de aviso, entra en el filtro y en el recuento de Vencido, y se ordena detrás de lo que va en
    /// rojo (decisión del 2026-10-23).
    /// </summary>
    public const int ClaveVencidoSinBloqueo = 2;

    /// <summary>
    /// Un número no es un nombre: <c>Enum.TryParse</c> aceptaría "4" como Vencido.
    /// Público (no <c>internal</c>) porque Application no expone sus internos a Web y
    /// <c>CriteriosExportacion</c> reutiliza este predicado para que el rastro de la exportación
    /// registre exactamente los estados que aplica la consulta, sin una copia propia.
    /// </summary>
    public static bool EsNombreDeEstado(string valor, out EstadoDocumento estado)
    {
        estado = default;
        return !int.TryParse(valor, out _) && Enum.TryParse(valor, out estado) && Enum.IsDefined(estado);
    }

    /// <summary>La <see cref="ClaveOrden"/> de lo que no es un estado de propietario (sin documentos, Faltante, En tolerancia).</summary>
    private const int ClaveSinEstadoDePropietario = 6;

    /// <summary>
    /// Clave de orden: primero lo que más urge. Lo malo conocido va antes que
    /// lo desconocido (<see cref="EstadoDocumento.SinConfirmar"/>), y esto
    /// antes que lo bueno conocido. Sin documentos va al final — no es peor que
    /// "vencido", solo desconocido.
    /// </summary>
    public static int ClaveOrden(EstadoDocumento? estado) => estado switch
    {
        EstadoDocumento.Vencido => 0,
        EstadoDocumento.Urgente => 1,
        EstadoDocumento.Proximo => 2,
        EstadoDocumento.SinConfirmar => 3,
        EstadoDocumento.Vigente => 4,
        EstadoDocumento.SinCaducidad => 5,
        _ => 6
    };
}
