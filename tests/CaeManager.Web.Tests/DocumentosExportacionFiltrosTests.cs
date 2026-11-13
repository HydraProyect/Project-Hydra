using System.Security.Claims;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentos;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Features.Documentos;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Http;

namespace CaeManager.Web.Tests;

/// <summary>
/// La exportación del listado de Documentos con «Vence en» (D-21) y «Pendiente de revisión IA»: el endpoint pasa a
/// <see cref="ObtenerDocumentosQuery"/> solo lo que el listado ofrece (7, 30 o 90 días; «1») y solo anota en el registro de
/// exportación los criterios que se aplicaron: el filtro de revisión IA no se atiende a Cliente, Consulta ni sin rol, y entonces ni
/// viaja ni queda anotado (el handler sigue siendo la autoridad).
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

        public Task RegistrarAsync(string entidadTipo, int filas, IReadOnlyDictionary<string, string> criterios,
            CancellationToken cancellationToken = default)
        {
            Criterios = criterios;
            return Task.CompletedTask;
        }
    }

    private sealed class MediadorCapturador : IMediator
    {
        public List<ObtenerDocumentosQuery> Consultas { get; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            if (request is ObtenerClientesAutorizadosQuery)
                return Task.FromResult((TResponse)(object)Array.Empty<ClienteAutorizadoDto>());

            var consulta = (ObtenerDocumentosQuery)(object)request;
            Consultas.Add(consulta);
            return Task.FromResult((TResponse)(object)new ResultadoPaginado<DocumentoListaDto>([], 0, consulta.Pagina, consulta.TamanoPagina));
        }
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest => throw new NotSupportedException();
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification => throw new NotSupportedException();
    }

    private static async Task<(ObtenerDocumentosQuery Consulta, IReadOnlyDictionary<string, string> Criterios)> ExportarAsync(
        string? rol, string? venceEn = null, string? pendienteRevisionIa = null)
    {
        var contexto = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(rol is null ? [] : [new Claim(ClaimTypes.Role, rol)], "prueba"))
        };
        var mediador = new MediadorCapturador();
        var registro = new RegistroCapturador();

        await DocumentosEndpoints.ExportarAsync(contexto, mediador, new TenantActualFalso(), registro, default,
            venceEn: venceEn, pendienteRevisionIa: pendienteRevisionIa);

        return (mediador.Consultas.Should().ContainSingle().Subject, registro.Criterios ?? new Dictionary<string, string>());
    }

    [Theory]
    [InlineData("7", 7)]
    [InlineData("30", 30)]
    [InlineData("90", 90)]
    public async Task Cada_plazo_que_ofrece_el_listado_llega_a_la_consulta_y_al_registro(string venceEn, int dias)
    {
        var (consulta, criterios) = await ExportarAsync(Roles.GestorCae, venceEn: venceEn);

        consulta.VenceEnDias.Should().Be(dias);
        criterios["venceEnDias"].Should().Be(venceEn);
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
        var (consulta, criterios) = await ExportarAsync(Roles.GestorCae, venceEn: venceEn);

        consulta.VenceEnDias.Should().BeNull();
        criterios.Should().NotContainKey("venceEnDias");
    }

    [Theory]
    [InlineData(Roles.Administrador)]
    [InlineData(Roles.DireccionCae)]
    [InlineData(Roles.CoordinadorCae)]
    [InlineData(Roles.GestorCae)]
    public async Task Con_un_rol_que_accede_a_la_cola_la_revision_IA_llega_a_la_consulta_y_se_anota(string rol)
    {
        var (consulta, criterios) = await ExportarAsync(rol, pendienteRevisionIa: "1");

        consulta.PendienteDeRevisionIa.Should().BeTrue();
        criterios["pendienteRevisionIa"].Should().Be("true");
    }

    [Theory]
    [InlineData(Roles.Cliente)]
    [InlineData(Roles.Consulta)]
    [InlineData("Inventado")]
    [InlineData(null)]
    public async Task Sin_acceso_a_la_cola_la_revision_IA_ni_llega_a_la_consulta_ni_se_anota_en_el_registro(string? rol)
    {
        var (consulta, criterios) = await ExportarAsync(rol, venceEn: "30", pendienteRevisionIa: "1");

        consulta.PendienteDeRevisionIa.Should().BeFalse();
        criterios.Should().NotContainKey("pendienteRevisionIa", "el handler no la aplicó: el registro no puede decir que sí");
        consulta.VenceEnDias.Should().Be(30, "control: «Vence en» no depende del rol");
    }

    [Theory]
    [InlineData("true")]
    [InlineData("0")]
    [InlineData("")]
    [InlineData(null)]
    public async Task Solo_el_valor_1_activa_la_revision_IA(string? valor)
    {
        var (consulta, criterios) = await ExportarAsync(Roles.GestorCae, pendienteRevisionIa: valor);

        consulta.PendienteDeRevisionIa.Should().BeFalse();
        criterios.Should().NotContainKey("pendienteRevisionIa");
    }
}
