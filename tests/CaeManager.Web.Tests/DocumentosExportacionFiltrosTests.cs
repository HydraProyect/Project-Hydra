using System.Security.Claims;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentos;
using CaeManager.Application.Documentos.Queries.ObtenerRevisionesIaPendientes;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Domain.Documentos;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Exportacion;
using CaeManager.Web.Features.Documentos;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace CaeManager.Web.Tests;

/// <summary>
/// La exportación del listado de Documentos con «Vence en» (D-21) y «Pendiente de revisión IA»: el endpoint pasa a
/// <see cref="ObtenerDocumentosQuery"/> solo lo que el listado ofrece (7, 30 o 90 días; «1»). Si la revisión IA se aplicó lo decide
/// el handler con el rol efectivo de cada lote y lo dice en <see cref="ResultadoPaginado{T}.FiltroRevisionIaAplicado"/>: el endpoint
/// anota lo que dijo el primer lote y, si un lote posterior dice otra cosa, aborta sin fichero ni registro.
/// </summary>
public class DocumentosExportacionFiltrosTests
{
    private sealed class TenantActualFalso : ITenantActual
    {
        public Guid? TenantId { get; } = Guid.NewGuid();
    }

    private sealed class RegistroCapturador : IRegistroExportacionService
    {
        public IReadOnlyDictionary<string, string>? Criterios { get; private set; }
        public int? Filas { get; private set; }

        public Task RegistrarAsync(string entidadTipo, int filas, IReadOnlyDictionary<string, string> criterios,
            CancellationToken cancellationToken = default)
        {
            Criterios = criterios;
            Filas = filas;
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Hace de handler: <paramref name="rolPorLote"/> da el rol efectivo con el que se resuelve cada lote (1, 2…), y el filtro se
    /// aplica, como en <see cref="ObtenerDocumentosQueryHandler"/>, si se pidió y ese rol accede a la cola. <paramref name="filasPorLote"/>
    /// da cuántas filas devuelve cada lote: un lote completo hace que el paginador pida el siguiente.
    /// </summary>
    private sealed class MediadorFalso(Func<int, string?> rolPorLote, Func<int, int> filasPorLote) : IMediator
    {
        public List<ObtenerDocumentosQuery> Consultas { get; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            if (request is ObtenerClientesAutorizadosQuery)
                return Task.FromResult((TResponse)(object)Array.Empty<ClienteAutorizadoDto>());

            var consulta = (ObtenerDocumentosQuery)(object)request;
            Consultas.Add(consulta);
            var filas = Enumerable.Range(0, filasPorLote(consulta.Pagina))
                .Select(_ => new DocumentoListaDto(Guid.NewGuid(), AmbitoAplicacion.Trabajador, "Propietario", "Tipo",
                    new DateOnly(2026, 1, 1), null, EstadoDocumento.Vigente, null, []))
                .ToList();
            return Task.FromResult((TResponse)(object)new ResultadoPaginado<DocumentoListaDto>(filas, filas.Count, consulta.Pagina, consulta.TamanoPagina)
            {
                FiltroRevisionIaAplicado = consulta.PendienteDeRevisionIa && ColaRevisionIa.AccedeElRol(rolPorLote(consulta.Pagina))
            });
        }
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest => throw new NotSupportedException();
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification => throw new NotSupportedException();
    }

    private sealed record Exportacion(IResult Resultado, IReadOnlyList<ObtenerDocumentosQuery> Consultas, RegistroCapturador Registro)
    {
        public ObtenerDocumentosQuery Consulta => Consultas.Should().ContainSingle().Subject;
        public IReadOnlyDictionary<string, string> Criterios => Registro.Criterios ?? new Dictionary<string, string>();
    }

    private static async Task<Exportacion> ExportarAsync(
        Func<int, string?> rolPorLote, string? venceEn = null, string? pendienteRevisionIa = null, Func<int, int>? filasPorLote = null)
    {
        var contexto = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([], "prueba")) };
        var mediador = new MediadorFalso(rolPorLote, filasPorLote ?? (_ => 0));
        var registro = new RegistroCapturador();

        var resultado = await DocumentosEndpoints.ExportarAsync(contexto, mediador, new TenantActualFalso(), registro, default,
            venceEn: venceEn, pendienteRevisionIa: pendienteRevisionIa);

        return new Exportacion(resultado, mediador.Consultas, registro);
    }

    private static Task<Exportacion> ExportarAsync(string? rol, string? venceEn = null, string? pendienteRevisionIa = null)
        => ExportarAsync(_ => rol, venceEn, pendienteRevisionIa);

    [Theory]
    [InlineData("7", 7)]
    [InlineData("30", 30)]
    [InlineData("90", 90)]
    public async Task Cada_plazo_que_ofrece_el_listado_llega_a_la_consulta_y_al_registro(string venceEn, int dias)
    {
        var exportacion = await ExportarAsync(Roles.GestorCae, venceEn: venceEn);

        exportacion.Consulta.VenceEnDias.Should().Be(dias);
        exportacion.Criterios["venceEnDias"].Should().Be(venceEn);
    }

    [Fact]
    public void Los_plazos_del_listado_son_una_sola_lista_compartida()
    {
        ObtenerDocumentosQuery.PlazosVenceEnDias.Should().Equal(7, 30, 90);
    }

    [Theory]
    [InlineData("45")]
    [InlineData("0")]
    [InlineData("-30")]
    [InlineData("+30")]
    [InlineData("030")]
    [InlineData(" 30")]
    [InlineData("30,90")]
    [InlineData("treinta")]
    [InlineData("")]
    [InlineData(null)]
    public async Task Un_plazo_que_el_listado_no_ofrece_se_ignora_y_no_se_anota(string? venceEn)
    {
        var exportacion = await ExportarAsync(Roles.GestorCae, venceEn: venceEn);

        exportacion.Consulta.VenceEnDias.Should().BeNull();
        exportacion.Criterios.Should().NotContainKey("venceEnDias");
    }

    [Theory]
    [InlineData(Roles.Administrador)]
    [InlineData(Roles.DireccionCae)]
    [InlineData(Roles.CoordinadorCae)]
    [InlineData(Roles.GestorCae)]
    public async Task Con_un_rol_que_accede_a_la_cola_la_revision_IA_llega_a_la_consulta_y_se_anota(string rol)
    {
        var exportacion = await ExportarAsync(rol, pendienteRevisionIa: "1");

        exportacion.Consulta.PendienteDeRevisionIa.Should().BeTrue();
        exportacion.Criterios["pendienteRevisionIa"].Should().Be("true");
    }

    /// <summary>
    /// El endpoint no decide por su cuenta: pasa lo pedido y el handler, que es la autoridad, lo ignora con un rol efectivo sin
    /// acceso a la cola (Usuario de Cliente, Consulta, desconocido o ninguno, como bajo una Sesión Privilegiada). El registro anota lo
    /// que el handler aplicó, no lo que se pidió.
    /// </summary>
    [Theory]
    [InlineData(Roles.Cliente)]
    [InlineData(Roles.Consulta)]
    [InlineData("Inventado")]
    [InlineData(null)]
    public async Task Sin_acceso_a_la_cola_el_handler_no_aplica_la_revision_IA_y_el_registro_no_la_anota(string? rol)
    {
        var exportacion = await ExportarAsync(rol, venceEn: "30", pendienteRevisionIa: "1");

        exportacion.Resultado.Should().BeOfType<FileStreamHttpResult>();
        exportacion.Criterios.Should().NotContainKey("pendienteRevisionIa", "el handler no la aplicó: el registro no puede decir que sí");
        exportacion.Consulta.VenceEnDias.Should().Be(30, "control: «Vence en» no depende del rol");
    }

    /// <summary>
    /// Hallazgo de la PR #2201: una Asignación heredada que pasa de Gestor CAE a Consulta a mitad de la exportación. El primer lote
    /// sale filtrado y el segundo no: servirlo mezclaría dos criterios en el fichero y el registro anotaría uno que no se aplicó a
    /// todo. Se aborta, en los dos sentidos del cambio, sin fichero y sin registro.
    /// </summary>
    [Theory]
    [InlineData(Roles.GestorCae, Roles.Consulta)]
    [InlineData(Roles.Consulta, Roles.GestorCae)]
    public async Task Si_el_rol_efectivo_cambia_entre_lotes_la_exportacion_se_aborta_sin_fichero_ni_registro(string rolPrimerLote, string rolDespues)
    {
        var exportacion = await ExportarAsync(lote => lote == 1 ? rolPrimerLote : rolDespues, pendienteRevisionIa: "1",
            filasPorLote: lote => lote == 1 ? PaginadorExportacion.TamanoLotePorDefecto : 3);

        exportacion.Consultas.Should().HaveCount(2, "el cambio se detecta en el segundo lote");
        exportacion.Resultado.Should().BeOfType<ProblemHttpResult>().Which.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        exportacion.Registro.Criterios.Should().BeNull("una exportación abortada no se sirve ni se anota");
    }

    private sealed class RelojAjustable(DateTimeOffset ahora) : TimeProvider
    {
        public DateTimeOffset Ahora { get; set; } = ahora;
        public override DateTimeOffset GetUtcNow() => Ahora;
    }

    /// <summary>
    /// Hallazgo de la PR #2201 (Codex, tercera pasada): una exportación que cruza la medianoche peninsular. El handler recalcula
    /// «Vence en», la franja de estado y el estado de cada fila con el día de negocio de cada lote, así que los lotes se calcularían con
    /// dos fechas y el Skip podría saltarse filas. Se aborta sin fichero y sin registro.
    /// </summary>
    [Fact]
    public async Task Si_el_dia_de_negocio_cambia_entre_lotes_la_exportacion_se_aborta_sin_fichero_ni_registro()
    {
        // 23:59 en Madrid (UTC+1 en noviembre); el segundo lote se resuelve a las 00:01 del día siguiente.
        var reloj = new RelojAjustable(new DateTimeOffset(2026, 11, 13, 22, 59, 0, TimeSpan.Zero));
        using var relojFijado = CaeManager.Domain.Common.DiaDeNegocio.FijarRelojEnEsteFlujo(reloj);

        var exportacion = await ExportarAsync(_ => Roles.GestorCae, venceEn: "30", filasPorLote: lote =>
        {
            if (lote == 2) reloj.Ahora = reloj.Ahora.AddMinutes(2);
            return lote == 1 ? PaginadorExportacion.TamanoLotePorDefecto : 3;
        });

        exportacion.Consultas.Should().HaveCount(2, "el cambio se detecta en el segundo lote");
        exportacion.Resultado.Should().BeOfType<ProblemHttpResult>().Which.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        exportacion.Registro.Criterios.Should().BeNull("una exportación abortada no se sirve ni se anota");
    }

    [Fact]
    public async Task Si_el_dia_de_negocio_no_cambia_varios_lotes_se_exportan_y_se_anotan()
    {
        var reloj = new RelojAjustable(new DateTimeOffset(2026, 11, 13, 10, 0, 0, TimeSpan.Zero));
        using var relojFijado = CaeManager.Domain.Common.DiaDeNegocio.FijarRelojEnEsteFlujo(reloj);

        var exportacion = await ExportarAsync(_ => Roles.GestorCae, venceEn: "30", filasPorLote: lote =>
        {
            reloj.Ahora = reloj.Ahora.AddMinutes(2);
            return lote == 1 ? PaginadorExportacion.TamanoLotePorDefecto : 3;
        });

        exportacion.Resultado.Should().BeOfType<FileStreamHttpResult>("control: el mismo día, aunque pase el tiempo, no aborta");
        exportacion.Criterios["venceEnDias"].Should().Be("30");
    }

    [Fact]
    public async Task Con_gestion_en_todos_los_lotes_se_exporta_todo_y_se_anota_la_revision_IA()
    {
        var exportacion = await ExportarAsync(_ => Roles.GestorCae, pendienteRevisionIa: "1",
            filasPorLote: lote => lote == 1 ? PaginadorExportacion.TamanoLotePorDefecto : 3);

        exportacion.Consultas.Should().HaveCount(2).And.OnlyContain(c => c.PendienteDeRevisionIa);
        exportacion.Resultado.Should().BeOfType<FileStreamHttpResult>();
        exportacion.Registro.Filas.Should().Be(PaginadorExportacion.TamanoLotePorDefecto + 3);
        exportacion.Criterios["pendienteRevisionIa"].Should().Be("true");
    }

    [Fact]
    public async Task Sin_pedir_la_revision_IA_un_cambio_de_rol_entre_lotes_no_aborta()
    {
        var exportacion = await ExportarAsync(lote => lote == 1 ? Roles.GestorCae : Roles.Consulta,
            filasPorLote: lote => lote == 1 ? PaginadorExportacion.TamanoLotePorDefecto : 3);

        exportacion.Resultado.Should().BeOfType<FileStreamHttpResult>("sin el filtro pedido, ningún lote lo aplica");
        exportacion.Registro.Filas.Should().Be(PaginadorExportacion.TamanoLotePorDefecto + 3);
        exportacion.Criterios.Should().NotContainKey("pendienteRevisionIa");
    }

    [Theory]
    [InlineData("true")]
    [InlineData("0")]
    [InlineData("")]
    [InlineData(null)]
    public async Task Solo_el_valor_1_activa_la_revision_IA(string? valor)
    {
        var exportacion = await ExportarAsync(Roles.GestorCae, pendienteRevisionIa: valor);

        exportacion.Consulta.PendienteDeRevisionIa.Should().BeFalse();
        exportacion.Criterios.Should().NotContainKey("pendienteRevisionIa");
    }
}
