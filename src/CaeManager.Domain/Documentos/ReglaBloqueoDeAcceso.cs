namespace CaeManager.Domain.Documentos;

/// <summary>
/// En qué situación está un requisito bloqueante (un <see cref="TipoDocumentoCentro"/> con
/// <see cref="TipoDocumentoCentro.BloqueaAcceso"/>) para su sujeto, hoy, en UN Centro.
/// </summary>
public enum SituacionDeRequisitoBloqueante
{
    /// <summary>Hay al menos un Documento del tipo que vale para acceder a ese Centro hoy (incluida la tolerancia): no bloquea.</summary>
    Cumplido = 0,

    /// <summary>No existe ningún Documento del tipo para ese sujeto: bloquea.</summary>
    Ausente = 1,

    /// <summary>
    /// Existen Documentos del tipo pero ninguno vale ya para acceder a ese Centro: todos vencieron y, si el Centro
    /// concede tolerancia, esta ya se agotó. Bloquea igual que el ausente.
    /// </summary>
    Vencido = 2,

    /// <summary>
    /// El documento vale en TALVEG, pero en la plataforma CAE de ese Centro está sin subir
    /// (<see cref="EstadoAcreditacion.PendienteDeSubir"/>; <see cref="ReglaPendienteEnPlataforma"/>, decisión del propietario,
    /// 2026-10-10). Bloquea al sujeto en ese Centro solo porque el tipo es requisito bloqueante allí
    /// (<see cref="TipoDocumentoCentro.BloqueaAcceso"/>, <see cref="ReglaBloqueoDeAcceso.AplicarPendienteEnPlataforma"/>).
    /// Sin tolerancia: el Pendiente no tiene fecha a la que sumarle días.
    /// </summary>
    PendienteDeSubirAPlataforma = 3,

    /// <summary>
    /// Como <see cref="PendienteDeSubirAPlataforma"/>, pero el documento ya está subido a la plataforma CAE del Centro y
    /// esta todavía no lo ha validado (<see cref="EstadoAcreditacion.Subida"/>).
    /// </summary>
    SinValidarEnPlataforma = 4
}

/// <summary>Un Documento visto desde la regla de acceso: su vigencia y la fecha de emisión (base de la periodicidad especial de un Centro).</summary>
public readonly record struct DocumentoParaAcceso(VigenciaDocumento Vigencia, DateOnly FechaEmision);

/// <summary>
/// Las condiciones de UN Centro para un Tipo de documento: la vigencia propia que impone
/// (<see cref="TipoDocumentoCentro.PeriodicidadEspecialMeses"/>) y los días de tolerancia ya resueltos
/// (<see cref="ReglaBloqueoDeAcceso.ResolverToleranciaDias"/>).
/// </summary>
public readonly record struct CondicionesDeAccesoDelCentro(int? PeriodicidadEspecialMeses, int ToleranciaDias);

/// <summary>
/// Resultado de evaluar un requisito bloqueante en un Centro.
/// </summary>
/// <param name="Situacion">
/// Cumplido, Ausente o Vencido (ya sin tolerancia); o pendiente en la plataforma del Centro, que pone
/// <see cref="ReglaBloqueoDeAcceso.AplicarPendienteEnPlataforma"/> sobre un Cumplido.
/// </param>
/// <param name="VencimientoEfectivo">
/// El vencimiento efectivo en este Centro del Documento más reciente de los que hay; <c>null</c> si no hay ninguno o
/// el mejor no vence. Con <see cref="Situacion"/> Vencido es la fecha desde la que dejó de valer.
/// </param>
/// <param name="EnToleranciaHasta">
/// Solo cuando <see cref="Situacion"/> es Cumplido <b>gracias a la tolerancia</b>: el último día en que el documento
/// vale para acceder a este Centro. Es un dato para mostrar («en tolerancia hasta X»); no cambia el estado de vigencia del
/// Documento, que sigue siendo Vencido.
/// </param>
public readonly record struct ResultadoDeRequisito(
    SituacionDeRequisitoBloqueante Situacion, DateOnly? VencimientoEfectivo, DateOnly? EnToleranciaHasta);

/// <summary>
/// <b>Punto único</b> de la regla «un documento bloqueante ausente o que ya no vale bloquea el acceso»
/// (decisión del propietario del producto, 2026-10-03, y su corrección de la tarde). Ninguna superficie decide por su
/// cuenta si un documento bloqueante «cuenta»: llaman aquí.
///
/// <list type="number">
/// <item><b>Se evalúa POR CENTRO.</b> Un documento bloquea el acceso a un Centro solo si ESE Centro lo exige como
/// bloqueante (<see cref="TipoDocumentoCentro.BloqueaAcceso"/>) y, con las condiciones de ese Centro (vigencia
/// propia y tolerancia), ya no vale. El mismo documento puede valer en un Centro y no en otro.</item>
/// <item><b>Sujeto Trabajador.</b> Un Documento bloqueante de ámbito Trabajador ausente o que ya no vale bloquea a
/// ESE Trabajador en ese Centro.</item>
/// <item><b>Sujeto Empresa.</b> Un Documento bloqueante de ámbito Empresa ausente o que ya no vale bloquea, en ese
/// Centro, a TODOS los Trabajadores de esa Empresa, aunque su documentación personal esté completa. Nunca en un
/// Centro que no lo exige. El modelo es egocéntrico por Tenant: nunca se cruza de un Tenant a otro.</item>
/// <item><b>«Bloqueado» es un estado del Trabajador, nunca del Centro.</b> El Centro enseña qué Trabajadores están
/// bloqueados y por qué.</item>
/// <item><b>Una alta nueva sin documentación está bloqueada</b> (sustituye a la advertencia de «alta nueva» del
/// 2026-08-16): no hay excepción por no haber completado el alta.</item>
/// <item><b>Pendiente en la plataforma</b> (decisión del 2026-10-10, <see cref="ReglaPendienteEnPlataforma"/>): un
/// documento que vale pero está sin subir o sin validar en la plataforma CAE del Centro bloquea al sujeto en ese Centro
/// <b>solo si ese Centro marca el tipo como bloqueante</b> (<see cref="TipoDocumentoCentro.BloqueaAcceso"/>): un RNT
/// bloqueante pendiente bloquea; una ISO opcional pendiente pone el Centro en «Pendiente» pero no bloquea a nadie. Lo
/// decide <see cref="AplicarPendienteEnPlataforma"/>, sobre el resultado de <see cref="Evaluar"/>.</item>
/// </list>
///
/// <para>
/// <b>Vencimiento efectivo en un Centro</b> (<see cref="VencimientoEfectivo"/>): si el Centro define una periodicidad
/// especial para el Tipo, el documento vence en la <b>fecha de emisión + esos meses</b> (sustituye al vencimiento que
/// trae el documento); si no, es el vencimiento del propio documento. «No caduca» nunca vence, tenga el Centro la
/// periodicidad que tenga. Un documento «Sin confirmar» no tiene vencimiento propio y no bloquea, salvo que el Centro
/// imponga periodicidad (entonces vence por emisión + meses, porque la vigencia la define el Centro y no hace falta
/// confirmar la del documento).
/// </para>
///
/// <para>
/// <b>Tolerancia</b> (<see cref="ValidoParaAcceder"/>): el documento sigue valiendo para el acceso hasta
/// <c>vencimiento efectivo + tolerancia</c> inclusive (con 15 días, vale el día 15 tras vencer y deja de valer el 16;
/// con 0, vale el día en que vence y no el siguiente). Esta regla solo decide el acceso y expone «en tolerancia
/// hasta X» como dato aparte (<see cref="ResultadoDeRequisito.EnToleranciaHasta"/>); el estado de vigencia del Documento
/// sigue siendo Vencido. Las vistas con contexto de Centro lo rotulan
/// <see cref="EstadoDocumento.EnTolerancia"/> con <see cref="EnToleranciaHasta"/> (las generales siguen mostrando «Vencido»).
/// Decisión del propietario (2026-10-03) todavía NO implementada aquí: un vencido dentro de la tolerancia cuenta como al día
/// en el porcentaje (incremento 2 del porcentaje, <c>EsConforme(ParDocumentalExigido)</c>).
/// </para>
///
/// <para>
/// Es una función pura, no un predicado SQL: EF no puede llamarla dentro de una consulta, así que quien la use trae
/// el estado, las fechas y las condiciones del Centro y la evalúa en memoria (copiar
/// <c>FechaVencimiento == null || FechaVencimiento &gt;= hoy</c> a una consulta es justo la copia que provocó
/// D-13/D-17/D-22; la vigila <c>ReglasDeNegocioSinCopiasTests</c>).
/// </para>
/// </summary>
public static class ReglaBloqueoDeAcceso
{
    /// <summary>
    /// La tolerancia que rige en un Centro para un Tipo: la personalización del Centro si existe
    /// (<see cref="TipoDocumentoCentro.ToleranciaDias"/>); si no, la del Cliente empresarial titular del Centro
    /// (<see cref="ToleranciaDocumentoClienteEmpresarial"/>); si no, 0.
    /// </summary>
    public static int ResolverToleranciaDias(int? delCentro, int? delClienteEmpresarial) =>
        delCentro ?? delClienteEmpresarial ?? 0;

    /// <summary>
    /// Cuándo vence el documento PARA ESTE CENTRO, o <c>null</c> si no vence (o no se sabe cuándo).
    /// </summary>
    public static DateOnly? VencimientoEfectivo(DocumentoParaAcceso documento, int? periodicidadEspecialMeses)
    {
        if (documento.Vigencia.Estado == EstadoVigenciaDocumento.NoCaduca)
            return null;

        if (periodicidadEspecialMeses is { } meses)
            return SumarMeses(documento.FechaEmision, meses);

        return documento.Vigencia.FechaVencimiento;
    }

    /// <summary>Último día en que el documento vale para acceder a este Centro; <c>null</c> si no vence.</summary>
    public static DateOnly? ValidoParaAccederHasta(DocumentoParaAcceso documento, CondicionesDeAccesoDelCentro condiciones) =>
        VencimientoEfectivo(documento, condiciones.PeriodicidadEspecialMeses) is { } vencimiento
            ? SumarDias(vencimiento, condiciones.ToleranciaDias)
            : null;

    /// <summary>
    /// Suma meses sin lanzar nunca: una periodicidad desmesurada (el Centro la guarda sin cota superior) satura en
    /// <see cref="DateOnly.MaxValue"/>. Esta función corre en memoria sobre TODOS los documentos de un Tenant para pintar Mi trabajo, la
    /// Bandeja e Inicio: una sola fila rara no puede dejar sin carga esas pantallas a todos los usuarios.
    /// </summary>
    private static DateOnly SumarMeses(DateOnly fecha, int meses)
    {
        var destino = (long)fecha.Year * 12 + (fecha.Month - 1) + meses;
        if (destino > 9999L * 12 + 11)
            return DateOnly.MaxValue;
        if (destino < 0)
            return DateOnly.MinValue;
        return fecha.AddMonths(meses);
    }

    /// <summary>Suma días saturando en <see cref="DateOnly.MaxValue"/> (un vencimiento centinela «9999-12-31» no puede lanzar).</summary>
    private static DateOnly SumarDias(DateOnly fecha, int dias) =>
        dias > DateOnly.MaxValue.DayNumber - fecha.DayNumber ? DateOnly.MaxValue : fecha.AddDays(dias);

    /// <summary>
    /// Si el documento está <b>vencido pero dentro de la tolerancia</b> en este Centro, el último día en que aún vale para
    /// acceder; <c>null</c> en cualquier otro caso (no vence, no ha vencido, o la tolerancia ya se agotó). Es lo que rotula
    /// «Vencido · en tolerancia hasta dd/MM» y la misma comparación que <see cref="Evaluar"/>, para que una vista de un
    /// documento y la decisión de acceso no puedan discrepar.
    /// </summary>
    public static DateOnly? EnToleranciaHasta(DocumentoParaAcceso documento, CondicionesDeAccesoDelCentro condiciones, DateOnly hoy)
    {
        if (VencimientoEfectivo(documento, condiciones.PeriodicidadEspecialMeses) is not { } vencimiento || vencimiento >= hoy)
            return null;

        var valeHasta = SumarDias(vencimiento, condiciones.ToleranciaDias);
        return ValeParaAcceder(valeHasta, hoy) ? valeHasta : null;
    }

    /// <summary>¿Vale este documento hoy para acceder a este Centro? Vencimiento efectivo + tolerancia &gt;= hoy.</summary>
    public static bool ValidoParaAcceder(DocumentoParaAcceso documento, CondicionesDeAccesoDelCentro condiciones, DateOnly hoy) =>
        ValeParaAcceder(ValidoParaAccederHasta(documento, condiciones), hoy);

    /// <summary>La comparación única con el último día en que vale (con la tolerancia ya sumada); sin fecha, siempre vale.</summary>
    private static bool ValeParaAcceder(DateOnly? valeHasta, DateOnly hoy) => valeHasta is not { } hasta || hasta >= hoy;

    /// <summary>
    /// Situación del requisito en este Centro dados TODOS los Documentos <b>operativos</b> de ese tipo que tiene el
    /// sujeto (un sustituido es historial y no entra: el vencido y su renovación no coexisten como operativos). Con más de
    /// un operativo —duplicados aún sin resolver—, basta uno que valga hoy para cumplir; unificarlo con el documento
    /// efectivo (<c>DocumentoEfectivo</c>, Application) llega con el destinatario nominativo (PR 6 del diseño).
    /// </summary>
    public static ResultadoDeRequisito Evaluar(
        IEnumerable<DocumentoParaAcceso> documentosDelTipo, CondicionesDeAccesoDelCentro condiciones, DateOnly hoy)
    {
        var existe = false;
        DateOnly? mejorVencimiento = null;
        DateOnly? mejorValeHasta = null;

        foreach (var documento in documentosDelTipo)
        {
            existe = true;
            var vencimiento = VencimientoEfectivo(documento, condiciones.PeriodicidadEspecialMeses);

            // Sin vencimiento, o que no ha vencido: vale sin necesidad de tolerancia.
            if (vencimiento is not { } v || v >= hoy)
                return new ResultadoDeRequisito(SituacionDeRequisitoBloqueante.Cumplido, vencimiento, null);

            if (mejorVencimiento is null || v > mejorVencimiento)
                mejorVencimiento = v;
            var valeHasta = ValidoParaAccederHasta(documento, condiciones)!.Value;
            if (mejorValeHasta is null || valeHasta > mejorValeHasta)
                mejorValeHasta = valeHasta;
        }

        if (!existe)
            return new ResultadoDeRequisito(SituacionDeRequisitoBloqueante.Ausente, null, null);

        return ValeParaAcceder(mejorValeHasta, hoy)
            ? new ResultadoDeRequisito(SituacionDeRequisitoBloqueante.Cumplido, mejorVencimiento, mejorValeHasta)
            : new ResultadoDeRequisito(SituacionDeRequisitoBloqueante.Vencido, mejorVencimiento, null);
    }

    /// <summary>Ausente, vencido y pendiente en la plataforma bloquean por igual; solo cumplido no bloquea.</summary>
    public static bool Bloquea(SituacionDeRequisitoBloqueante situacion) =>
        situacion != SituacionDeRequisitoBloqueante.Cumplido;

    /// <summary>¿Es una de las dos situaciones de Pendiente en la plataforma (sin subir o sin validar)?</summary>
    public static bool EsPendienteEnPlataforma(SituacionDeRequisitoBloqueante situacion) =>
        situacion is SituacionDeRequisitoBloqueante.PendienteDeSubirAPlataforma or SituacionDeRequisitoBloqueante.SinValidarEnPlataforma;

    /// <summary>
    /// Pendiente en la plataforma (decisión del propietario, 2026-10-10): cómo queda un requisito cuando el documento del
    /// sujeto está pendiente en la plataforma CAE del Centro (<see cref="ReglaPendienteEnPlataforma.CuentaEnElCentro"/>,
    /// ya comprobado por quien llama).
    ///
    /// <list type="bullet">
    /// <item><b>Sin marca de bloqueo, no bloquea.</b> <paramref name="requisitoBloqueante"/> es <c>null</c> cuando el Centro
    /// no marca el tipo con <see cref="TipoDocumentoCentro.BloqueaAcceso"/>: el pendiente no tiene requisito al que
    /// aplicarse y se devuelve <c>null</c> (una ISO opcional pendiente no bloquea a nadie).</item>
    /// <item><b>Con marca, un cumplido pasa a pendiente</b>: <see cref="SituacionDeRequisitoBloqueante.PendienteDeSubirAPlataforma"/>
    /// o <see cref="SituacionDeRequisitoBloqueante.SinValidarEnPlataforma"/>, sin tolerancia.</item>
    /// <item><b>Ausente o vencido se quedan como están</b>: ya bloquean y son la causa más grave.</item>
    /// <item>Un estado de acreditación que no es pendiente (validada, rechazada, no requerida) no cambia nada aquí; la
    /// rechazada es causa propia del Centro (D-7).</item>
    /// </list>
    /// </summary>
    public static ResultadoDeRequisito? AplicarPendienteEnPlataforma(
        ResultadoDeRequisito? requisitoBloqueante, EstadoAcreditacion estadoAcreditacion)
    {
        if (requisitoBloqueante is not { } requisito)
            return null;

        if (requisito.Situacion != SituacionDeRequisitoBloqueante.Cumplido || !ReglaPendienteEnPlataforma.EstaPendiente(estadoAcreditacion))
            return requisito;

        var situacion = estadoAcreditacion == EstadoAcreditacion.Subida
            ? SituacionDeRequisitoBloqueante.SinValidarEnPlataforma
            : SituacionDeRequisitoBloqueante.PendienteDeSubirAPlataforma;
        return new ResultadoDeRequisito(situacion, requisito.VencimientoEfectivo, null);
    }

    /// <summary>
    /// Qué ámbitos de tipo pueden ser requisito bloqueante, porque tienen un sujeto del bloqueo: el Trabajador
    /// o la Empresa. Un tipo de Cliente, Vehículo o Proyecto marcado como bloqueante no tiene sujeto definido y
    /// no bloquea a nadie (no se inventa una regla para ellos).
    /// </summary>
    public static bool AmbitoPuedeBloquear(AmbitoAplicacion ambito) =>
        ambito is AmbitoAplicacion.Trabajador or AmbitoAplicacion.Empresa;
}
