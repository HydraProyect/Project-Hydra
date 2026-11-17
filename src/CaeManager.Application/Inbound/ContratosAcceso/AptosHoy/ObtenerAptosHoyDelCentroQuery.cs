using CaeManager.Application.Common;
using CaeManager.Application.Inbound.ContratosAcceso.EvidenciasInbound;
using CaeManager.Application.Inbound.Requisitos;
using CaeManager.Application.Tenants.Encargo;
using CaeManager.Application.TiposDocumento;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Inbound.ContratosAcceso;
using CaeManager.Domain.Inbound.EvidenciasInbound;
using CaeManager.Domain.Inbound.Requisitos;
using MediatR;
using Microsoft.EntityFrameworkCore;

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

    /// <summary>La evidencia de aptitud médica transcribe «No apto». Solo se sabe que no acredita; la conclusión no sale.</summary>
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
/// </param>
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
    DateOnly? FechaVencimiento);

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
/// NoApto. Con «Fija N meses» el vencimiento es el más temprano entre la emisión más N meses y la fecha del documento (D4).
/// Un requisito de la Empresa contratista que bloquea y falla deja NoApto a todos sus Trabajadores (D3). Un Trabajador es Apto
/// solo si todo lo que bloquea, suyo y de su Empresa, es Apto o EnTolerancia.
/// </para>
///
/// <para>
/// <b>Huecos declarados.</b> (1) Solo aparecen los Trabajadores con alguna evidencia (D2 i): quien no tiene ninguna no sale.
/// (2) No hay resolución por capas (B-15): solo cuentan las plantillas del Centro y las exigencias de su Cliente empresarial,
/// no las de Actividad ni de riesgo. (3) <c>SePideA</c> y los tipos de contratación no se evalúan: el requisito se aplica a
/// todos (falla cerrado). (4) Los requisitos de vehículo o máquina no entran. (5) Con el mismo requisito en las dos plantillas,
/// cada una se evalúa por separado y basta que una bloquee y falle. (6) Un requisito que toma el vencimiento del documento y
/// una evidencia sin fecha no se da por vigente. (7) Una evidencia médica que transcribe «No apto» no acredita el requisito,
/// aunque esté Aceptada y vigente. (8) Cada evidencia es de un contrato: un Trabajador en dos contratos se evalúa en cada uno
/// y sale una sola vez, con el estado más favorable.
/// </para>
/// </summary>
public class ObtenerAptosHoyDelCentroQueryHandler(
    IContratosAccesoQueryContext contratosContext,
    IEvidenciasInboundQueryContext evidenciasContext,
    IPlantillasRequisitosQueryContext plantillasContext,
    ITiposDocumentoQueryContext tiposContext,
    IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerAptosHoyDelCentroQuery, IReadOnlyList<AptoHoyDto>>
{
    public async Task<IReadOnlyList<AptoHoyDto>> Handle(ObtenerAptosHoyDelCentroQuery request, CancellationToken cancellationToken)
    {
        if (!await alcanceDatos.OperaDesdeElLadoDeGestionAsync(cancellationToken))
            return [];
        var centrosVisibles = await alcanceDatos.ObtenerCentroIdsVisiblesAsync(cancellationToken);
        if (centrosVisibles is not null && !centrosVisibles.Contains(request.CentroId))
            return [];

        var hoy = DiaDeNegocio.Hoy();
        var centroId = request.CentroId;
        var candidatos = await contratosContext.ContratosAcceso
            .Where(c => c.CentroId == centroId && c.Estado == EstadoContratoAcceso.Activo && c.TenantContratistaId != null)
            .Where(ContratoAcceso.EstaVigenteEn(hoy))
            .OrderByDescending(c => c.FechaInicio).ThenBy(c => c.Id)
            .ToListAsync(cancellationToken);

        var contratos = new List<ContratoAcceso>();
        foreach (var contrato in candidatos)
        {
            if (await ContratoAccesoAutorizacion.VisibleAsync(contrato.ClienteEmpresarialId, alcanceDatos, cancellationToken))
                contratos.Add(contrato);
        }

        if (contratos.Count == 0)
            return [];

        var contratoIds = contratos.Select(c => c.Id).ToList();
        var evidencias = await evidenciasContext.EvidenciasInbound
            .Where(e => contratoIds.Contains(e.ContratoAccesoId))
            .ToListAsync(cancellationToken);

        var requisitos = await RequisitosQueBloqueanAsync(centroId, contratos, cancellationToken);
        var tipoIds = requisitos.Select(r => r.TipoDocumentoId).Distinct().ToList();
        var tipos = tipoIds.Count == 0
            ? new Dictionary<Guid, TipoDocumento>()
            : await tiposContext.TiposDocumento.Where(t => tipoIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, cancellationToken);

        var exigidos = requisitos
            .Select(r =>
            {
                tipos.TryGetValue(r.TipoDocumentoId, out var tipo);
                return new Exigido(
                    r, tipo?.Nombre ?? "Requisito sin nombre", tipo is null ? 0 : r.ToleranciaEfectiva(tipo));
            })
            .OrderBy(x => x.Nombre, StringComparer.CurrentCultureIgnoreCase).ThenBy(x => x.Requisito.Id)
            .ToList();

        var filas = new Dictionary<Guid, AptoHoyDto>();
        foreach (var contrato in contratos)
        {
            var delContrato = evidencias.Where(e => e.ContratoAccesoId == contrato.Id).ToList();
            var evaluadasEmpresa = Evaluar(
                exigidos.Where(x => x.Requisito.Entidad == EntidadRequisito.Empresa).ToList(),
                delContrato.Where(e => e.TrabajadorOrigenId is null).ToList(), hoy, SujetoDelRequisitoInbound.EmpresaContratista);
            var exigidosTrabajador = exigidos.Where(x => x.Requisito.Entidad == EntidadRequisito.Trabajador).ToList();

            foreach (var porTrabajador in delContrato.Where(e => e.TrabajadorOrigenId is not null).GroupBy(e => e.TrabajadorOrigenId!.Value))
            {
                var propias = porTrabajador.ToList();
                var evaluadas = evaluadasEmpresa.Concat(
                    Evaluar(exigidosTrabajador, propias, hoy, SujetoDelRequisitoInbound.Trabajador)).ToList();
                var identidad = propias.OrderByDescending(e => e.ValidadaEnUtc).ThenBy(e => e.Id).First();
                var fila = Resumir(porTrabajador.Key, identidad, contrato, evaluadas, hoy);

                if (!filas.TryGetValue(porTrabajador.Key, out var previa) || fila.Estado < previa.Estado)
                    filas[porTrabajador.Key] = fila;
            }
        }

        return filas.Values
            .OrderByDescending(f => f.Estado).ThenBy(f => f.Nombre, StringComparer.CurrentCultureIgnoreCase).ThenBy(f => f.TrabajadorId)
            .ToList();
    }

    private async Task<IReadOnlyList<RequisitoPlantilla>> RequisitosQueBloqueanAsync(
        Guid centroId, IReadOnlyList<ContratoAcceso> contratos, CancellationToken cancellationToken)
    {
        var clientesEmpresariales = contratos.Select(c => c.ClienteEmpresarialId).Distinct().ToList();
        var plantillaIds = await plantillasContext.PlantillasRequisitos
            .Where(p => p.Activa
                && (p.Tipo == TipoPlantillaRequisitos.DeCentro && p.CentroId == centroId
                    || p.Tipo == TipoPlantillaRequisitos.ExigenciasClienteEmpresarial
                    && p.ClienteEmpresarialId != null && clientesEmpresariales.Contains(p.ClienteEmpresarialId.Value)))
            .Select(p => p.Id)
            .ToListAsync(cancellationToken);
        if (plantillaIds.Count == 0)
            return [];

        return await plantillasContext.RequisitosPlantilla
            .Where(r => plantillaIds.Contains(r.PlantillaId) && r.Activo && r.BloqueaAcceso
                && r.Entidad != EntidadRequisito.VehiculoOMaquina)
            .ToListAsync(cancellationToken);
    }

    private static List<Evaluado> Evaluar(
        IReadOnlyList<Exigido> exigidos, IReadOnlyList<EvidenciaInbound> delSujeto, DateOnly hoy, SujetoDelRequisitoInbound sujeto) =>
        exigidos
            .Select(x => new Evaluado(
                x.Nombre, sujeto,
                MejorDe(x, delSujeto.Where(e => e.TipoDocumentoId == x.Requisito.TipoDocumentoId
                    && EvidenciasInboundProyeccion.EstaAceptada(e)), hoy)))
            .ToList();

    private static AptoHoyDto Resumir(
        Guid trabajadorId, EvidenciaInbound identidad, ContratoAcceso contrato, IReadOnlyList<Evaluado> evaluadas, DateOnly hoy)
    {
        // Las evaluaciones de la Empresa contratista van primero en la lista: lo que deja fuera a todos (D3) se cita antes que
        // lo propio del Trabajador.
        var peor = evaluadas.Where(e => e.Resultado.Estado == EstadoAptitudInbound.NoApto).Select(e => (Evaluado?)e).FirstOrDefault()
                   ?? evaluadas.Where(e => e.Resultado.Estado == EstadoAptitudInbound.EnTolerancia).Select(e => (Evaluado?)e).FirstOrDefault();

        var estado = peor?.Resultado.Estado ?? EstadoAptitudInbound.Apto;
        var proximo = evaluadas
            .Select(e => e.Resultado.Vencimiento)
            .Where(v => v is { } dia && dia >= hoy)
            .Min();

        return new AptoHoyDto(
            trabajadorId, identidad.SujetoNombre, identidad.SujetoDocumentoIdentidad, contrato.Id, contrato.EmpresaContratistaId,
            contrato.Tipo, estado,
            peor?.Nombre, peor?.Sujeto,
            estado == EstadoAptitudInbound.NoApto ? peor!.Resultado.Causa : null,
            peor is null ? proximo : peor.Resultado.Vencimiento);
    }

    private static Resultado MejorDe(Exigido exigido, IEnumerable<EvidenciaInbound> candidatas, DateOnly hoy)
    {
        var resultados = candidatas.Select(e => Valorar(exigido, e, hoy)).ToList();
        if (resultados.Count == 0)
            return new Resultado(EstadoAptitudInbound.NoApto, CausaNoAptitudInbound.SinDocumentoValidado, null);

        return resultados
            .OrderBy(r => Rango(r))
            .ThenByDescending(r => r.Vencimiento ?? DateOnly.MaxValue)
            .First();
    }

    private static int Rango(Resultado r) => r.Estado switch
    {
        EstadoAptitudInbound.Apto => 0,
        EstadoAptitudInbound.EnTolerancia => 1,
        _ => r.Causa switch
        {
            CausaNoAptitudInbound.Vencido => 2,
            CausaNoAptitudInbound.SinFechaDeVencimiento => 3,
            _ => 4,
        },
    };

    private static Resultado Valorar(Exigido exigido, EvidenciaInbound evidencia, DateOnly hoy)
    {
        // Una conclusión «No apto» transcrita no acredita nada, aunque la evidencia esté Aceptada y en plazo: solo se sabe
        // que no acredita, no por qué.
        if (evidencia.SoloConclusionAptitud && evidencia.ConclusionAptitud == ConclusionAptitud.NoApto)
            return new Resultado(EstadoAptitudInbound.NoApto, CausaNoAptitudInbound.NoAcreditado, null);

        var requisito = exigido.Requisito;
        if (requisito.ModoVigencia == ModoVigenciaRequisito.SinVencimiento)
            return new Resultado(EstadoAptitudInbound.Apto, null, null);

        DateOnly? vencimiento = requisito.ModoVigencia switch
        {
            // D4: la fecha más temprana entre la emisión más N meses y la del documento.
            ModoVigenciaRequisito.Fija => MasTemprana(
                requisito.VigenciaMeses is { } meses ? evidencia.FechaEmision.AddMonths(meses) : null, evidencia.FechaVencimiento),
            _ => evidencia.FechaVencimiento,
        };
        if (vencimiento is not { } dia)
            return new Resultado(EstadoAptitudInbound.NoApto, CausaNoAptitudInbound.SinFechaDeVencimiento, null);

        if (hoy.DayNumber <= dia.DayNumber)
            return new Resultado(EstadoAptitudInbound.Apto, null, dia);
        if (hoy.DayNumber <= dia.DayNumber + exigido.ToleranciaDias)
            return new Resultado(EstadoAptitudInbound.EnTolerancia, null, dia);
        return new Resultado(EstadoAptitudInbound.NoApto, CausaNoAptitudInbound.Vencido, dia);
    }

    private static DateOnly? MasTemprana(DateOnly? a, DateOnly? b) =>
        a is null ? b : b is null ? a : a < b ? a : b;

    private sealed record Exigido(RequisitoPlantilla Requisito, string Nombre, int ToleranciaDias);

    private sealed record Resultado(EstadoAptitudInbound Estado, CausaNoAptitudInbound? Causa, DateOnly? Vencimiento);

    private sealed record Evaluado(string Nombre, SujetoDelRequisitoInbound Sujeto, Resultado Resultado);
}
