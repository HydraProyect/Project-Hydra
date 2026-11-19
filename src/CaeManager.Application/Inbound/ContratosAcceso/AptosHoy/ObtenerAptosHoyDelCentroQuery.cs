using CaeManager.Application.Common;
using CaeManager.Application.Inbound.Autorizacion;
using CaeManager.Application.Inbound.ContratosAcceso.EvidenciasInbound;
using CaeManager.Application.Inbound.Requisitos;
using CaeManager.Application.Tenants.Encargo;
using CaeManager.Application.TiposDocumento;
using CaeManager.Domain.Common;
using CaeManager.Domain.Inbound.ContratosAcceso;
using CaeManager.Domain.Inbound.EvidenciasInbound;
using MediatR;

namespace CaeManager.Application.Inbound.ContratosAcceso.AptosHoy;

/// <summary>El estado de un Trabajador para entrar hoy en el Centro (pestaña «Aptos hoy»).</summary>
public enum EstadoAptitudInbound
{
    Apto = 0,

    /// <summary>Un requisito que bloquea está vencido pero dentro de su tolerancia: puede entrar (Q-10).</summary>
    EnTolerancia = 1,

    NoApto = 2,
}

/// <summary>Por qué un requisito que bloquea no se da por acreditado. Nunca lleva contenido clínico.</summary>
public enum CausaNoAptitudInbound
{
    /// <summary>No hay ninguna evidencia Aceptada de ese tipo de documento para ese sujeto.</summary>
    SinDocumentoValidado = 1,

    /// <summary>La evidencia está vencida y fuera de tolerancia.</summary>
    Vencido = 2,

    /// <summary>El requisito toma el vencimiento del documento y la evidencia no lo trae: no se puede dar por vigente.</summary>
    SinFechaDeVencimiento = 3,

    /// <summary>
    /// Un requisito cuyo tipo de documento revela salud (art. 9 RGPD) no se acredita, sea cual sea la causa: sin documento,
    /// vencido, sin fecha o con un «No apto» transcrito. Es la única causa que sale para esos requisitos, y sin fecha, para que
    /// la causa no delate la conclusión médica (decisión del 2026-11-13: el certificado de aptitud solo dice «vencido»).
    /// </summary>
    NoAcreditado = 4,
}

/// <summary>A quién se le pide el requisito que bloquea o vence.</summary>
public enum SujetoDelRequisitoInbound
{
    Trabajador = 1,
    EmpresaContratista = 2,
}

/// <summary>
/// Una fila de «Aptos hoy». Lleva el nombre del requisito que bloquea o vence y, como mucho, una fecha: no existe campo para
/// la conclusión médica, su motivo ni el archivo (en la aptitud médica solo se muestra el nombre del requisito).
/// </summary>
/// <param name="TrabajadorId">El Trabajador en el Tenant de la Empresa contratista (sin FK), tal como lo copió la evidencia.</param>
/// <param name="Requisito">
/// Con <see cref="EstadoAptitudInbound.NoApto"/> y <see cref="EstadoAptitudInbound.EnTolerancia"/>, el requisito que bloquea o
/// que está en tolerancia; nulo con <see cref="EstadoAptitudInbound.Apto"/>.
/// </param>
/// <param name="FechaVencimiento">
/// El vencimiento del requisito citado; con <see cref="EstadoAptitudInbound.Apto"/>, el próximo vencimiento de lo que bloquea.
/// Nulo en un requisito que revela salud y no se acredita (<see cref="CausaNoAptitudInbound.NoAcreditado"/>).
/// </param>
/// <param name="EmpresasContratistaIds">
/// Todas las Empresas contratistas de los contratos de acceso del Trabajador en el Centro, la de <paramref name="EmpresaContratistaId"/>
/// incluida: el Trabajador sale en una sola fila, con el estado menos favorable, y el filtro por Empresa contratista lo encuentra
/// por cualquiera de ellas. Solo ids de Empresa, que la proyección restringida ya muestra.
/// </param>
/// <remarks>
/// Proyección completa (con requisito, sujeto, causa y fecha) solo para quien tiene <see cref="CapacidadInbound.ValidarInbound"/>,
/// el Gestor CAE (DECISION_LOG del 2026-11-14, duda 6). Para los demás roles con <see cref="CapacidadInbound.LeerInbound"/>,
/// Consulta incluido, la proyección restringida: nombre, DNI parcial, Empresa contratista y apto o no apto («en tolerancia» cuenta
/// como apto, Q-10), con tipo de contrato, requisito, sujeto, causa y fecha a null y <c>ProyeccionCompleta</c> a false.
/// </remarks>
public record AptoHoyDto(
    Guid TrabajadorId,
    string Nombre,
    string? DocumentoIdentidad,
    Guid ContratoAccesoId,
    Guid EmpresaContratistaId,
    TipoContratoAcceso? TipoContrato,
    EstadoAptitudInbound Estado,
    string? Requisito,
    SujetoDelRequisitoInbound? SujetoDelRequisito,
    CausaNoAptitudInbound? Causa,
    DateOnly? FechaVencimiento,
    bool ProyeccionCompleta = true,
    IReadOnlyList<Guid>? EmpresasContratistaIds = null);

/// <summary>
/// El DNI parcial de «Aptos hoy»: los cuatro últimos caracteres, como el del Importador. Aplicarlo dos veces da lo mismo. Vive
/// aquí porque Inbound no depende de las pantallas de Outbound (ratchet Modulos-Inbound-a-Outbound, cerrado).
/// </summary>
public static class DniParcialAptosHoy
{
    public static string Enmascarar(string? documento)
    {
        var limpio = documento?.Trim();
        return string.IsNullOrEmpty(limpio) ? string.Empty : limpio.Length <= 4 ? "****" : "****" + limpio[^4..];
    }
}

/// <summary>
/// «Aptos hoy» del Centro: para cada Trabajador con alguna evidencia Inbound en el Tenant del Cliente empresarial, su estado en
/// el día de negocio de hoy (<see cref="DiaDeNegocio.Hoy()"/>).
/// </summary>
[AreaDelEncargo("ContratosAcceso")]
public record ObtenerAptosHoyDelCentroQuery(Guid CentroId) : IRequest<IReadOnlyList<AptoHoyDto>>;

/// <summary>
/// Lee solo dentro del Tenant del Cliente empresarial (contratos, evidencias, plantillas y tipos de documento propios), con RLS
/// normal y sin funciones entre Tenants: no abre ningún documento del Tenant de la Empresa contratista. Un contrato que la
/// Asignación de Cartera del actor no deja ver (<see cref="ContratoAccesoAutorizacion.VisibleAsync"/>), un Centro fuera de su
/// alcance o un usuario de portal dan una lista vacía, sin decir cuál de las condiciones falla.
///
/// <para>
/// <b>Regla.</b> Por cada contrato de acceso Activo, vinculado y vigente hoy en el Centro, y cada requisito activo que bloquea
/// (plantilla del Centro y exigencias del Cliente empresarial): hay una evidencia Aceptada del mismo tipo de documento y
/// vigente hoy, Apto; vencida dentro de <see cref="RequisitoPlantilla.ToleranciaEfectiva"/>, EnTolerancia (Q-10); si no,
/// NoApto. Con «Fija N meses» el vencimiento es el más temprano entre la emisión más N meses y la fecha del documento (D4); con
/// «Sin vencimiento» vale la fecha del documento si la trae y, si no, la evidencia no vence. En un requisito que revela salud
/// la causa de un NoApto es siempre <see cref="CausaNoAptitudInbound.NoAcreditado"/>, sin fecha (art. 9 RGPD).
/// Un requisito de la Empresa contratista que bloquea y falla deja NoApto a todos sus Trabajadores (D3). Un Trabajador es Apto
/// solo si todo lo que bloquea, suyo y de su Empresa, es Apto o EnTolerancia.
/// </para>
///
/// <para>
/// <b>Huecos declarados.</b> (1) Solo aparecen los Trabajadores con alguna evidencia (D2 i): quien no tiene ninguna no sale.
/// (2) No hay resolución por capas (B-15): solo cuentan las plantillas del Centro y las exigencias de su Cliente empresarial,
/// no las de Actividad ni de riesgo. (3) Ni la máscara de tipos de contratación ni <c>SePideA</c> se evalúan aquí: cada
/// requisito de Trabajador se aplica a todos los Trabajadores del Centro (falla cerrado). La pantalla Requerimientos del Tenant
/// TEC (<c>ObtenerRequerimientosTecQuery</c>, #2342) sí aplica la máscara al Trabajador, donde es el único criterio y
/// <c>SePideA</c> no se lee; este cálculo todavía no la adopta, y adoptarla cambia quién sale NoApto, así que es otra línea de
/// trabajo y no se hace aquí. (4) Los requisitos de vehículo o máquina no entran. (5) Con el mismo requisito en las dos plantillas,
/// cada una se evalúa por separado y basta que una bloquee y falle. (6) Un requisito que toma el vencimiento del documento y
/// una evidencia sin fecha no se da por vigente. (7) Una evidencia médica que transcribe «No apto» no acredita el requisito y,
/// mientras esté Aceptada y vigente (su vencimiento, sin tolerancia; sin fecha que la caduque, se da por vigente), bloquea
/// siempre, aunque haya otra evidencia mejor del mismo tipo (decisión del 2026-11-17, P3 punto 2); caducada, deja de bloquear,
/// pero con él Aceptado, vigente o caducado, solo acredita un apto emitido después de la emisión del «No apto» (el anterior o
/// el emitido el mismo día no revive). Esta última regla, la lectura de los «No apto» de todos los contratos del Centro y la
/// proyección restringida de abajo son una lectura que falla cerrado de la regla de Chris «un No apto vigente bloquea
/// siempre, con independencia de otras evidencias», decidida por la subcoordinadora de Inbound el 2026-11-17 tras la
/// revisión de la PR #2329; si Chris la entiende de otro modo, se corrige aquí. Solo se evalúa si el Centro o el Cliente
/// empresarial tienen ese tipo como requisito que bloquea. (7 bis) El «No apto» se lee de las evidencias del mismo Trabajador
/// (<c>TrabajadorOrigenId</c>) en todos los contratos de acceso del Centro que el actor ve, sea cual sea su estado (los que
/// cuentan y los Suspendido, Finalizado, Cancelado o no vigentes hoy), con el mismo contexto y la misma RLS del Tenant del
/// Cliente empresarial, y se suma como ajeno a cada contrato para el bloqueo y para el corte de la regla anterior: un «No apto»
/// de un contrato que cuenta también evalúa a los demás contratos del Trabajador (si no, un apto anterior a ese «No apto»
/// revivía en otro contrato). De los contratos que no cuentan no sale ninguna fila ni ninguna acreditación positiva, y el
/// Trabajador sin ninguna evidencia en un contrato que cuenta sigue sin aparecer (hueco 1). Un «No apto» de un contrato que la
/// Asignación de Cartera del actor no deja ver no se lee (no se cambia la autorización). (8) Cada evidencia es de un
/// contrato: un Trabajador en dos contratos se evalúa en cada uno y sale una sola vez, con el estado menos favorable de todos
/// ellos (decisión del 2026-11-17, P3 punto 4); la fila lleva en <see cref="AptoHoyDto.EmpresasContratistaIds"/> todas sus
/// Empresas (solo las de los contratos que cuentan). Cita, además, el contrato y la Empresa contratista de donde sale el
/// estado, con dos propiedades a la vez: (a) con el mismo estado en varios contratos gana la fila cuyo estado procede de las
/// evidencias propias de su contrato (y no de un «No apto» ajeno) y, si no hay ninguna así o hay varias, la del contrato más
/// reciente; (b) si el estado de la fila procede de un «No apto» de otro contrato, la proyección completa cita el contrato que
/// lo tiene (el vigente que bloquea o, si es por el corte, el de emisión más reciente), con su Empresa e identidad, salvo que
/// ese contrato no cuente (no tiene fila ni Empresa en el listado). En la proyección restringida la fila cita, en cambio, el
/// contrato, la Empresa y la identidad (nombre y DNI parcial) del contrato más reciente que
/// cuenta. (9) En una fila Apto de
/// la proyección completa, el próximo vencimiento puede ser el del certificado de aptitud (una revisión anticipada que fija el médico); solo lo ve quien tiene
/// <see cref="CapacidadInbound.ValidarInbound"/>, que ya puede leer la conclusión en las evidencias.
/// </para>
/// </summary>
public class ObtenerAptosHoyDelCentroQueryHandler(
    IContratosAccesoQueryContext contratosContext,
    IEvidenciasInboundQueryContext evidenciasContext,
    IPlantillasRequisitosQueryContext plantillasContext,
    ITiposDocumentoQueryContext tiposContext,
    IAlcanceDatosService alcanceDatos,
    IAutorizacionInbound autorizacionInbound)
    : IRequestHandler<ObtenerAptosHoyDelCentroQuery, IReadOnlyList<AptoHoyDto>>
{
    public async Task<IReadOnlyList<AptoHoyDto>> Handle(ObtenerAptosHoyDelCentroQuery request, CancellationToken cancellationToken)
    {
        // B-03: la misma puerta que el listado de contratos de acceso; sin ella el papel Outbound en el lado del Tenant TCE,
        // una Sesión Privilegiada sin rol o un usuario de portal leerían evidencias que Inbound les niega.
        if (!await autorizacionInbound.TieneAsync(CapacidadInbound.LeerInbound, cancellationToken))
            return [];
        // Duda 6 (DECISION_LOG del 2026-11-14): fuera del Gestor CAE, nunca el motivo. La proyección completa pide Validar.
        var completa = await autorizacionInbound.TieneAsync(CapacidadInbound.ValidarInbound, cancellationToken);

        // La evaluación (alcance del actor, contratos, requisitos y evidencias) vive en EvaluadorAptitudInbound, que también usa la
        // comprobación de credencial; aquí solo queda la autorización de la capacidad y la proyección por rol.
        var evaluador = new EvaluadorAptitudInbound(contratosContext, evidenciasContext, plantillasContext, tiposContext, alcanceDatos);
        var evaluacion = await evaluador.EvaluarAsync(request.CentroId, null, cancellationToken);

        // La restricción va antes de ordenar: el orden no puede delatar un «en tolerancia» que la proyección restringida oculta.
        return evaluacion.Trabajadores
            .Select(x => completa
                ? CitarElContratoDelNoApto(x.Dto, x.ContratoOrigenAjenoId, evaluacion.ContratosPorId, evaluacion.Identidades)
                : Restringida(x.Dto, x.ContratoMasReciente, x.IdentidadMasReciente))
            .OrderByDescending(f => f.Estado).ThenBy(f => f.Nombre, StringComparer.CurrentCultureIgnoreCase).ThenBy(f => f.TrabajadorId)
            .ToList();
    }

    /// <summary>
    /// Proyección completa: si el estado de la fila depende de un «No apto» de otro contrato (la fila salió de un contrato cuyas
    /// evidencias propias no lo dan), cita el contrato, la Empresa contratista y la identidad del contrato que tiene ese «No apto»,
    /// no el contrato que solo lo sufre. Si ese contrato no cuenta (Suspendido, Finalizado...) no se cita: no tiene fila ni
    /// Empresa en el listado, y la fila se queda con el contrato que la produjo.
    /// </summary>
    private static AptoHoyDto CitarElContratoDelNoApto(
        AptoHoyDto fila, Guid? contratoOrigenId, IReadOnlyDictionary<Guid, ContratoAcceso> contratos,
        IReadOnlyDictionary<(Guid Trabajador, Guid Contrato), EvidenciaInbound> identidades)
    {
        if (contratoOrigenId is not { } origen
            || !contratos.TryGetValue(origen, out var citado)
            || !identidades.TryGetValue((fila.TrabajadorId, origen), out var identidad))
            return fila;

        return fila with
        {
            ContratoAccesoId = citado.Id,
            EmpresaContratistaId = citado.EmpresaContratistaId,
            TipoContrato = citado.Tipo,
            Nombre = identidad.SujetoNombre,
            DocumentoIdentidad = identidad.SujetoDocumentoIdentidad,
        };
    }

    /// <summary>
    /// La proyección para quien no tiene <see cref="CapacidadInbound.ValidarInbound"/>: nombre, DNI parcial, Empresa contratista
    /// y apto o no apto. Sin tipo de contrato, requisito, sujeto, causa ni fecha; «en tolerancia» cuenta como apto (Q-10). La fila
    /// cita el contrato, la Empresa contratista y la identidad (nombre y documento) del contrato más reciente que cuenta, no los del
    /// estado menos favorable: citar estos delataría en qué contrato está el «en tolerancia» o el motivo (decisión del 2026-11-17,
    /// P3, puntos 5 y revisión de la ronda 6).
    /// </summary>
    private static AptoHoyDto Restringida(AptoHoyDto fila, ContratoAcceso masReciente, EvidenciaInbound identidad) => fila with
    {
        ContratoAccesoId = masReciente.Id,
        EmpresaContratistaId = masReciente.EmpresaContratistaId,
        Nombre = identidad.SujetoNombre,
        DocumentoIdentidad = DniParcialAptosHoy.Enmascarar(identidad.SujetoDocumentoIdentidad),
        TipoContrato = null,
        ProyeccionCompleta = false,
        Estado = fila.Estado == EstadoAptitudInbound.NoApto ? EstadoAptitudInbound.NoApto : EstadoAptitudInbound.Apto,
        Requisito = null,
        SujetoDelRequisito = null,
        Causa = null,
        FechaVencimiento = null,
    };
}
