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
    Vencido = 2
}

/// <summary>
/// Un Documento visto desde la regla de acceso: su vigencia propia, la fecha de emisión (ancla de la periodicidad especial de
/// un Centro cuando el Documento nunca se presentó en él) y, si se presentó, la fecha de su última presentación EN EL CENTRO
/// evaluado (<see cref="PresentacionDocumentoEnCentro"/>; es un dato de la pareja Documento × Centro, no del Documento).
/// </summary>
public readonly record struct DocumentoParaAcceso(
    VigenciaDocumento Vigencia, DateOnly FechaEmision, DateOnly? UltimaPresentacionEnElCentro = null);

/// <summary>
/// Las condiciones de UN Centro para un Tipo de documento: cada cuántos meses exige volver a presentarlo
/// (<see cref="TipoDocumentoCentro.PeriodicidadEspecialMeses"/>) y los días de tolerancia ya resueltos
/// (<see cref="ReglaBloqueoDeAcceso.ResolverToleranciaDias"/>).
/// </summary>

public readonly record struct CondicionesDeAccesoDelCentro(int? PeriodicidadEspecialMeses, int ToleranciaDias);

/// <summary>
/// Resultado de evaluar un requisito bloqueante en un Centro.
/// </summary>
/// <param name="Situacion">Cumplido, Ausente o Vencido (ya sin tolerancia).</param>
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
/// El estado de un Documento <b>visto desde un Centro</b> (<see cref="ReglaBloqueoDeAcceso.EstadoEnElCentro"/>): lo que se
/// enseña en Centro 360 y en Trabajador 360 por Centro.
/// </summary>
/// <param name="Estado">Estado de contexto: el de <see cref="CalculadoraEstadoDocumento"/> sobre el vencimiento efectivo en el Centro, o <see cref="EstadoDocumento.EnTolerancia"/>.</param>
/// <param name="VenceEnElCentro">
/// Solo con valor cuando el Centro define periodicidad especial para el Tipo y el documento vence: cuándo vence EN ESTE
/// Centro, que puede ser antes que su vigencia propia. <c>null</c> en cualquier otro caso (sin periodicidad, «No caduca»).
/// </param>
/// <param name="EnToleranciaHasta">Solo con <see cref="EstadoDocumento.EnTolerancia"/>: el último día en que aún vale para acceder a este Centro.</param>
public readonly record struct EstadoDeDocumentoEnElCentro(EstadoDocumento Estado, DateOnly? VenceEnElCentro, DateOnly? EnToleranciaHasta);

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
/// </list>
///
/// <para>
/// <b>Vencimiento efectivo en un Centro</b> (<see cref="VencimientoEfectivo"/>): si el Centro define una periodicidad
/// especial para el Tipo, el documento vence en el Centro en
/// <c>min(última presentación en el Centro + esos meses, vigencia propia del documento)</c>; si nunca se presentó en ese
/// Centro, el ancla es la fecha de emisión (<see cref="PresentacionDocumentoEnCentro"/>). El documento sigue valiendo en
/// los demás Centros por su vigencia propia. Sin periodicidad es el vencimiento del propio documento. «No caduca» nunca
/// vence, tenga el Centro la periodicidad que tenga (contrato vigente, decisión del propietario: no se ha cambiado). Un
/// documento «Sin confirmar» no tiene vencimiento propio y no bloquea, salvo que el Centro imponga periodicidad (entonces
/// vence en el ancla + meses, porque no hay vigencia propia que acote).
/// </para>
///
/// <para>
/// <b>Estado en un Centro</b> (<see cref="EstadoEnElCentro"/>): el estado de contexto que ven Centro 360 y Trabajador 360
/// por Centro. Es el estado de <see cref="CalculadoraEstadoDocumento"/> sobre el vencimiento efectivo en ese Centro (Vigente,
/// Próximo, Urgente, Vencido) y «En tolerancia» si ya venció allí y la tolerancia se aplica sobre ESE vencimiento. El porcentaje
/// de cumplimiento NO lo usa: sigue midiendo el estado real del documento.
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
    /// Cuándo vence el documento PARA ESTE CENTRO, o <c>null</c> si no vence (o no se sabe cuándo): sin periodicidad especial
    /// del Centro, su vigencia propia; con ella, <c>min(ancla + meses, vigencia propia)</c> donde el ancla es la última
    /// presentación en el Centro y, si no hay ninguna, la fecha de emisión.
    /// </summary>
    public static DateOnly? VencimientoEfectivo(DocumentoParaAcceso documento, int? periodicidadEspecialMeses)
    {
        if (documento.Vigencia.Estado == EstadoVigenciaDocumento.NoCaduca)
            return null;

        var propio = documento.Vigencia.FechaVencimiento;
        if (periodicidadEspecialMeses is not { } meses)
            return propio;

        var ancla = documento.UltimaPresentacionEnElCentro ?? documento.FechaEmision;
        var delCentro = SumarMeses(ancla, meses);
        return propio is { } vigenciaPropia && vigenciaPropia < delCentro ? vigenciaPropia : delCentro;
    }

    /// <summary>
    /// El estado de contexto de un Documento en un Centro. <b>Una sola función</b> para el bloqueo, Centro 360, Trabajador 360
    /// por Centro y Mi trabajo: calcula el estado con <see cref="CalculadoraEstadoDocumento"/> sobre la vigencia efectiva en el
    /// Centro (<see cref="VencimientoEfectivo"/>) y, si ya venció allí y su tolerancia (resuelta por
    /// <see cref="ResolverToleranciaDias"/>) todavía no se agotó, lo rotula <see cref="EstadoDocumento.EnTolerancia"/>. La tolerancia
    /// nunca entra en el porcentaje de cumplimiento: ese mide el estado real del documento y no usa esta función.
    /// </summary>
    public static EstadoDeDocumentoEnElCentro EstadoEnElCentro(
        DocumentoParaAcceso documento, CondicionesDeAccesoDelCentro condiciones, DateOnly hoy, int umbralAmbarDias, int umbralRojoDias)
    {
        var vencimiento = VencimientoEfectivo(documento, condiciones.PeriodicidadEspecialMeses);
        var vigenciaEnElCentro = documento.Vigencia.Estado == EstadoVigenciaDocumento.NoCaduca
            ? VigenciaDocumento.NoCaduca
            : VigenciaDocumento.DesdeFechaOpcional(vencimiento);

        var estado = CalculadoraEstadoDocumento.Calcular(vigenciaEnElCentro, hoy, umbralAmbarDias, umbralRojoDias);
        var venceEnElCentro = condiciones.PeriodicidadEspecialMeses is null ? null : vencimiento;

        if (estado == EstadoDocumento.Vencido && EnToleranciaHasta(documento, condiciones, hoy) is { } hasta)
            return new EstadoDeDocumentoEnElCentro(EstadoDocumento.EnTolerancia, venceEnElCentro, hasta);

        return new EstadoDeDocumentoEnElCentro(estado, venceEnElCentro, null);
    }

    /// <summary>
    /// ¿Puede el Gestor CAE «volver a presentar» este Documento a este Centro? Comparten la función el comando y la pantalla
    /// (el DTO), para que no discrepen. Solo si el Centro exige periodicidad para el Tipo (<paramref name="condiciones"/> la trae
    /// solo de una fila Incluida), el Documento es operativo y sigue vigente por su fecha propia: un Documento vencido se
    /// renueva (otro Documento), no se vuelve a presentar; uno que «No caduca» no vence y no tiene qué reiniciar. Sin vigencia
    /// confirmada sí puede: no está vencido y la periodicidad es la del Centro.
    /// </summary>
    public static bool PuedeVolverAPresentar(
        DocumentoParaAcceso documento, CondicionesDeAccesoDelCentro condiciones, DateOnly hoy, bool esOperativo)
    {
        if (!esOperativo || condiciones.PeriodicidadEspecialMeses is null)
            return false;

        if (documento.Vigencia.Estado == EstadoVigenciaDocumento.NoCaduca)
            return false;

        // Vigente por su fecha propia (sin tolerancia): la misma comparación única que el resto de la regla.
        return ValeParaAcceder(documento.Vigencia.FechaVencimiento, hoy);
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

    /// <summary>Ausente y vencido bloquean por igual; solo cumplido no bloquea.</summary>
    public static bool Bloquea(SituacionDeRequisitoBloqueante situacion) =>
        situacion != SituacionDeRequisitoBloqueante.Cumplido;

    /// <summary>
    /// Qué ámbitos de tipo pueden ser requisito bloqueante, porque tienen un sujeto del bloqueo: el Trabajador
    /// o la Empresa. Un tipo de Cliente, Vehículo o Proyecto marcado como bloqueante no tiene sujeto definido y
    /// no bloquea a nadie (no se inventa una regla para ellos).
    /// </summary>
    public static bool AmbitoPuedeBloquear(AmbitoAplicacion ambito) =>
        ambito is AmbitoAplicacion.Trabajador or AmbitoAplicacion.Empresa;
}
