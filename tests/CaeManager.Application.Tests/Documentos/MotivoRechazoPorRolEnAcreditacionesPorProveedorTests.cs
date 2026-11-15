using CaeManager.Application.Documentos.Queries.ObtenerAcreditacionesPorProveedor;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Tests.TiposDocumento;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Integraciones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Documentos;

/// <summary>
/// Decisión de Chris del 2026-11-15 («Tanda de decisiones pendientes del 15-11 tarde»): el motivo del historial de rechazos
/// de una acreditación, donde también queda el de un rechazo anotado a mano, no llega a Consulta ni al rol Cliente por
/// <see cref="ObtenerAcreditacionesPorProveedorQuery"/>. Cada caso envía la petición exacta de una vía: la Bandeja del Gestor
/// (<c>ObtenerBandejaGestorQuery</c>), la pestaña Plataformas de Empresa 360 (<c>FilasPlataformasEmpresa.Consulta</c>), la
/// extensión (<c>GET /extension/acreditaciones-pendientes</c>, que solo trae rechazos pasados) y la pestaña Plataforma de
/// Documentos (<c>PlataformaTab</c>). El resto de la fila (estado y dirección del portal) sigue llegando a Consulta.
/// </summary>
public class MotivoRechazoPorRolEnAcreditacionesPorProveedorTests
{
    private const string Motivo = "Anotado a mano: falta la firma del responsable";

    public enum Via { Bandeja, Empresa360, Extension, PlataformaTab }

    public static TheoryData<Via, string, bool> Casos()
    {
        var casos = new TheoryData<Via, string, bool>();
        foreach (var via in Enum.GetValues<Via>())
        {
            casos.Add(via, "Administrador", true);
            casos.Add(via, "DireccionCae", true);
            casos.Add(via, "CoordinadorCae", true);
            casos.Add(via, "GestorCae", true);
            casos.Add(via, "Consulta", false);
            casos.Add(via, "Cliente", false);
        }
        return casos;
    }

    [Theory]
    [MemberData(nameof(Casos))]
    public async Task El_motivo_de_un_rechazo_anotado_a_mano_solo_llega_a_los_roles_de_la_trazabilidad_interna(
        Via via, string rol, bool recibeMotivo)
    {
        var e = new Escenario(rechazoVigente: via != Via.Extension);

        var fila = (await e.Handler(rol).Handle(e.Peticion(via), CancellationToken.None))
            .SelectMany(p => p.Clientes).SelectMany(c => c.Documentos)
            .Should().ContainSingle("la fila de la acreditación llega a todos los roles de gestión").Subject;

        fila.UltimoMotivoRechazo.Should().Be(recibeMotivo ? Motivo : null);
        fila.UrlAccesoCanal.Should().Be(Escenario.Url, "el corte es solo del motivo: la dirección del portal sigue la regla operativa");
    }

    [Fact]
    public async Task Sin_rol_efectivo_el_motivo_no_viaja()
    {
        var e = new Escenario(rechazoVigente: true);

        var fila = (await e.Handler(rol: null).Handle(new ObtenerAcreditacionesPorProveedorQuery(), CancellationToken.None))
            .SelectMany(p => p.Clientes).SelectMany(c => c.Documentos).Single();

        fila.UltimoMotivoRechazo.Should().BeNull("falla en cerrado");
    }

    [Fact]
    public async Task El_usuario_de_Cliente_con_su_alcance_real_no_recibe_filas()
    {
        var e = new Escenario(rechazoVigente: true);

        var resultado = await e.Handler("Cliente", new AlcanceDatosServiceFalso(centroIdsParaGestion: []))
            .Handle(new ObtenerAcreditacionesPorProveedorQuery(), CancellationToken.None);

        resultado.Should().BeEmpty("su lista de Centros de gestión es vacía: ni la fila ni el motivo");
    }

    private sealed class Escenario
    {
        public const string Url = "https://portal.ejemplo.test";

        private readonly DocumentosQueryContextFalso _documentos = new();
        private readonly CentrosQueryContextFalso _centros = new();
        private readonly ProveedoresPlataformaCaeQueryContextFalso _proveedores = new();
        private readonly TiposDocumentoQueryContextFalso _tipos = new();
        private readonly Guid _empresaId = Guid.NewGuid();

        public Escenario(bool rechazoVigente)
        {
            var proveedor = new ProveedorPlataformaCae("dokify", "Dokify");
            var centro = new Centro(Guid.NewGuid(), Guid.NewGuid(), "Centro de Trabajo");
            var canal = CanalGestionDocumental.DePlataforma(centro.Id, "Portal", proveedor.Id, Url, null, null);
            var tipo = new TipoDocumento("Certificado", null, false, 1, AmbitoAplicacion.Empresa, RequisitoDocumental.Si);
            var documento = Documento.DeEmpresa(_empresaId, tipo.Id, new DateOnly(2026, 1, 1), VigenciaDocumento.NoCaduca);
            var acreditacion = new AcreditacionDocumentoPlataforma(documento.Id, canal.Id);
            acreditacion.CambiarEstadoAMano(EstadoAcreditacion.Rechazada, new DateOnly(2026, 11, 10), Motivo, "Nota interna",
                Guid.NewGuid(), DateTime.UtcNow.AddMinutes(-10));
            if (!rechazoVigente)
                // La extensión excluye los rechazos vigentes: el motivo que le llega es el de un rechazo pasado.
                acreditacion.CambiarEstadoAMano(EstadoAcreditacion.PendienteDeSubir, new DateOnly(2026, 11, 11), null, null,
                    Guid.NewGuid(), DateTime.UtcNow);

            _documentos.ListaDocumentos.Add(documento);
            _documentos.ListaAcreditacionesDocumentoPlataforma.Add(acreditacion);
            _documentos.ListaRechazosAcreditacionDocumentoPlataforma.AddRange(acreditacion.HistorialRechazos);
            _centros.ListaCentros.Add(centro);
            _centros.ListaCanalesGestionDocumental.Add(canal);
            _proveedores.ListaProveedores.Add(proveedor);
            _tipos.ListaTiposDocumento.Add(tipo);
        }

        public ObtenerAcreditacionesPorProveedorQueryHandler Handler(string? rol, AlcanceDatosServiceFalso? alcance = null) => new(
            _documentos, _centros, _proveedores, new TrabajadoresQueryContextFalso(), new EmpresasQueryContextFalso(), _tipos,
            alcance ?? new AlcanceDatosServiceFalso(), new CurrentUserServiceFalso(Guid.NewGuid(), rol));

        /// <summary>La petición que envía cada vía, copiada de su llamador.</summary>
        public ObtenerAcreditacionesPorProveedorQuery Peticion(Via via) => via switch
        {
            Via.Bandeja => new ObtenerAcreditacionesPorProveedorQuery(),
            Via.Empresa360 => new ObtenerAcreditacionesPorProveedorQuery(
                IncluirAceptadas: true, IncluirSubidas: true, SoloDocumentosDeEmpresaId: _empresaId),
            Via.Extension => new ObtenerAcreditacionesPorProveedorQuery(SoloSubiblesExtension: true, IncluirDominios: true),
            Via.PlataformaTab => new ObtenerAcreditacionesPorProveedorQuery(IncluirAceptadas: true, IncluirSubidas: true),
            _ => throw new ArgumentOutOfRangeException(nameof(via)),
        };
    }
}
