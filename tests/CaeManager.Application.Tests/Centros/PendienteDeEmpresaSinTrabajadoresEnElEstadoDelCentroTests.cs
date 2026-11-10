using CaeManager.Application.Centros;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Proyectos;
using CaeManager.Application.Tests.Reportes;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Trabajadores;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Centros;

/// <summary>
/// La consecuencia visible de la decisión del 2026-11-08 (un documento de Empresa pendiente cuenta en los Centros de su Empresa
/// aunque no tengan Trabajadores asignados), pasando por el servicio REAL <see cref="CalculoEstadoCentroService"/>: el estado
/// «Pendiente» del Centro, su recuento de causas pendientes (el que materializa <c>PendientesEnPlataforma</c>) y el porcentaje
/// de cumplimiento, cuyo denominador sale del mismo universo que el Pendiente. Sobre contextos falsos, sin Postgres; el
/// cargador solo (<see cref="PendientesEnPlataformaDeCentrosTests"/>) no prueba nada de esto.
/// </summary>
public class PendienteDeEmpresaSinTrabajadoresEnElEstadoDelCentroTests
{
    private readonly MundoAcreditaciones _mundo = new();
    private readonly ConfiguracionQueryContextFalso _configuracion = new();
    private readonly EmpresasQueryContextFalso _empresas = new();
    private readonly ProyectosQueryContextFalso _proyectos = new();

    public PendienteDeEmpresaSinTrabajadoresEnElEstadoDelCentroTests() =>
        _configuracion.ListaParametrosSistema.Add(new ParametroSistema(umbralAmbarDias: 30, umbralRojoDias: 7));

    private CalculoEstadoCentroService Calculo() =>
        new(_mundo.CentrosContexto, _mundo.DocumentosContexto, _mundo.TiposDocumentoContexto, _mundo.TrabajadoresContexto,
            _mundo.AsignacionesContexto, _configuracion, _empresas, _proyectos, _proyectos);

    /// <summary>
    /// Un Centro de la Empresa del mundo con plataforma, que exige el RNT de Empresa como bloqueante, y el documento de
    /// Empresa acreditado ante la plataforma sin subir.
    /// </summary>
    private (Centro Centro, TipoDocumento Tipo) CentroQueExigeElRntDeSuEmpresa(Guid? empresaDelCentroId = null)
    {
        var centro = _mundo.Centro(empresaId: empresaDelCentroId ?? _mundo.EmpresaId);
        var plataforma = _mundo.AccesoPlataforma(centro);
        var tipo = _mundo.Tipo(RequisitoDocumental.Si, AmbitoAplicacion.Empresa, "RNT");
        _mundo.TiposDocumentoContexto.ListaTiposDocumentoCentros.Add(
            new TipoDocumentoCentro(tipo.Id, centro.Id, incluido: true, bloqueaAcceso: true));
        var documento = _mundo.DocumentoDeEmpresa(tipo);
        _mundo.DocumentosContexto.ListaAcreditacionesDocumentoPlataforma.Add(
            new AcreditacionDocumentoPlataforma(documento.Id, plataforma.Id));
        return (centro, tipo);
    }

    [Fact]
    public async Task Un_Centro_sin_Trabajadores_con_el_documento_de_su_Empresa_pendiente_esta_Pendiente_y_su_cumplimiento_lo_cuenta()
    {
        var (centro, tipo) = CentroQueExigeElRntDeSuEmpresa();

        var estado = (await Calculo().CalcularAsync([centro.Id], CancellationToken.None))[centro.Id];
        var cumplimiento = (await Calculo().CalcularCumplimientoAsync([centro.Id], CancellationToken.None))[centro.Id];
        var paresDeEmpresa = await Calculo().ObtenerParesDeEmpresaExigidosAsync([centro.Id], CancellationToken.None);

        estado.Estado.Should().Be(EstadoCentro.Pendiente, "nadie está asignado, pero el Centro exige el RNT de su Empresa y está sin subir");
        estado.Causas.Count(c => c.PendienteEnPlataforma).Should().Be(1, "es el recuento que se materializa como PendientesEnPlataforma");
        cumplimiento.Requeridos.Should().Be(1, "el Pendiente y el denominador salen del mismo universo: no es un «sin requisitos» 0/0");
        cumplimiento.AlDia.Should().Be(0, "un documento pendiente en la plataforma no está al día");
        cumplimiento.Porcentaje.Should().Be(0);
        paresDeEmpresa.Should().ContainSingle(
            "las filas de pares materializadas salen del mismo universo que el porcentaje del Centro")
            .Which.Should().Match<ParDocumentalDeEmpresaExigido>(p =>
                p.CentroId == centro.Id && p.EmpresaId == _mundo.EmpresaId && p.TipoDocumentoId == tipo.Id);
    }

    [Fact]
    public async Task Un_Centro_con_Trabajadores_solo_de_otra_Empresa_cuenta_tambien_el_par_de_su_propia_Empresa_pendiente()
    {
        var (centro, tipo) = CentroQueExigeElRntDeSuEmpresa();
        var otraEmpresaId = Guid.NewGuid();
        var trabajadorAjeno = Domain.Trabajadores.Trabajador.DeEmpresa(otraEmpresaId, "Luis", "Gómez", "87654321X");
        _mundo.TrabajadoresContexto.ListaTrabajadores.Add(trabajadorAjeno);
        _mundo.Asignacion(trabajadorAjeno, centro);

        var cumplimiento = (await Calculo().CalcularCumplimientoAsync([centro.Id], CancellationToken.None))[centro.Id];
        var paresDeEmpresa = await Calculo().ObtenerParesDeEmpresaExigidosAsync([centro.Id], CancellationToken.None);

        cumplimiento.Requeridos.Should().Be(2, "el RNT de la Empresa del Centro (pendiente) y el de la Empresa del Trabajador (faltante)");
        cumplimiento.AlDia.Should().Be(0);
        paresDeEmpresa.Select(p => (p.EmpresaId, p.TipoDocumentoId)).Should().BeEquivalentTo(
            [(_mundo.EmpresaId, tipo.Id), (otraEmpresaId, tipo.Id)]);
    }

    [Fact]
    public async Task El_documento_pendiente_de_una_Empresa_en_un_Centro_de_otra_sin_Trabajadores_suyos_no_cuenta_ni_pone_al_Centro_Pendiente()
    {
        var (centro, _) = CentroQueExigeElRntDeSuEmpresa(empresaDelCentroId: Guid.NewGuid());

        var estado = (await Calculo().CalcularAsync([centro.Id], CancellationToken.None))[centro.Id];
        var cumplimiento = (await Calculo().CalcularCumplimientoAsync([centro.Id], CancellationToken.None))[centro.Id];

        estado.Estado.Should().NotBe(EstadoCentro.Pendiente, "la Empresa no es la del Centro ni tiene Trabajadores asignados allí");
        cumplimiento.Requeridos.Should().Be(0, "control: sin Pendiente que contar, no hay par");
    }
}
