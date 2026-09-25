using CaeManager.Application.Documentos.Acreditacion;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Documentos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Documentos;

/// <summary>
/// Regla única de alta de acreditaciones de plataforma (P0-7): un Documento se
/// acredita ante cada acceso de plataforma de cada Centro donde su propietario
/// trabaja y que exige su tipo; nunca ante un Centro que no lo exige; nunca dos
/// veces el mismo par (Documento, acceso).
/// </summary>
public class AltaAcreditacionesPlataformaServiceTests
{
    private static readonly DateOnly Hoy = new(2026, 1, 15);

    private static Documento DocumentoNuevoDe(Domain.Trabajadores.Trabajador trabajador, TipoDocumento tipo) =>
        Documento.DeTrabajador(trabajador.Id, tipo.Id, Hoy, VigenciaDocumento.VenceEl(Hoy.AddYears(5)));

    [Fact]
    public async Task Un_documento_nuevo_se_acredita_ante_cada_acceso_de_plataforma_del_centro_que_exige_su_tipo()
    {
        var mundo = new MundoAcreditaciones();
        var trabajador = mundo.Trabajador();
        var centro = mundo.Centro();
        var plataformaA = mundo.AccesoPlataforma(centro);
        var plataformaB = mundo.AccesoPlataforma(centro);
        mundo.AccesoCorreo(centro);
        mundo.Asignacion(trabajador, centro);
        var tipo = mundo.Tipo(RequisitoDocumental.Si);
        var documento = DocumentoNuevoDe(trabajador, tipo);

        var agregadas = await mundo.Servicio().AgregarPendientesAsync(new AltasConAcreditacion { Documentos = [documento] });

        agregadas.Should().Be(2);
        mundo.Agregadas.Should().BeEquivalentTo([(documento.Id, plataformaA.Id), (documento.Id, plataformaB.Id)],
            "un acceso por correo no es una plataforma donde acreditar");
        mundo.Acreditaciones.Acreditaciones.Should().OnlyContain(a => a.Estado == EstadoAcreditacion.PendienteDeSubir);
    }

    [Fact]
    public async Task Un_centro_que_no_exige_el_tipo_no_recibe_la_acreditacion_y_el_que_lo_exige_si()
    {
        var mundo = new MundoAcreditaciones();
        var trabajador = mundo.Trabajador();
        var tipo = mundo.Tipo(RequisitoDocumental.No);

        var centroQueLoExige = mundo.Centro("Planta Norte");
        var accesoQueLoExige = mundo.AccesoPlataforma(centroQueLoExige);
        mundo.Requisito(tipo, centroQueLoExige, incluido: true);
        mundo.Asignacion(trabajador, centroQueLoExige);

        // Sin fila explícita y con el tipo no obligatorio por defecto: no lo exige.
        var centroSinFila = mundo.Centro("Planta Sur");
        mundo.AccesoPlataforma(centroSinFila);
        mundo.Asignacion(trabajador, centroSinFila);

        var documento = DocumentoNuevoDe(trabajador, tipo);

        await mundo.Servicio().AgregarPendientesAsync(new AltasConAcreditacion { Documentos = [documento] });

        mundo.Agregadas.Should().BeEquivalentTo([(documento.Id, accesoQueLoExige.Id)]);
    }

    [Fact]
    public async Task Un_centro_que_excluye_expresamente_un_tipo_obligatorio_no_recibe_la_acreditacion()
    {
        var mundo = new MundoAcreditaciones();
        var trabajador = mundo.Trabajador();
        var tipo = mundo.Tipo(RequisitoDocumental.Si);
        var centro = mundo.Centro();
        mundo.AccesoPlataforma(centro);
        mundo.Requisito(tipo, centro, incluido: false);
        mundo.Asignacion(trabajador, centro);

        var agregadas = await mundo.Servicio().AgregarPendientesAsync(
            new AltasConAcreditacion { Documentos = [DocumentoNuevoDe(trabajador, tipo)] });

        agregadas.Should().Be(0, "la fila explícita del Centro manda sobre el valor por defecto del tipo");
    }

    [Fact]
    public async Task Un_centro_sin_acceso_de_plataforma_no_recibe_acreditaciones()
    {
        var mundo = new MundoAcreditaciones();
        var trabajador = mundo.Trabajador();
        var centro = mundo.Centro();
        mundo.AccesoCorreo(centro);
        mundo.Asignacion(trabajador, centro);
        var tipo = mundo.Tipo(RequisitoDocumental.Si);

        var agregadas = await mundo.Servicio().AgregarPendientesAsync(
            new AltasConAcreditacion { Documentos = [DocumentoNuevoDe(trabajador, tipo)] });

        agregadas.Should().Be(0);
    }

    [Fact]
    public async Task Una_asignacion_cerrada_no_lleva_el_documento_a_ese_centro()
    {
        var mundo = new MundoAcreditaciones();
        var trabajador = mundo.Trabajador();
        var centro = mundo.Centro();
        mundo.AccesoPlataforma(centro);
        mundo.Asignacion(trabajador, centro).CerrarPorAmbitoEliminado(Hoy);
        var tipo = mundo.Tipo(RequisitoDocumental.Si);

        var agregadas = await mundo.Servicio().AgregarPendientesAsync(
            new AltasConAcreditacion { Documentos = [DocumentoNuevoDe(trabajador, tipo)] });

        agregadas.Should().Be(0);
    }

    [Fact]
    public async Task Un_documento_de_la_empresa_llega_al_centro_a_traves_de_sus_trabajadores_asignados()
    {
        var mundo = new MundoAcreditaciones();
        var trabajador = mundo.Trabajador();
        var centro = mundo.Centro();
        var acceso = mundo.AccesoPlataforma(centro);
        mundo.Asignacion(trabajador, centro);
        var tipo = mundo.Tipo(RequisitoDocumental.Si, AmbitoAplicacion.Empresa, "Seguro RC");
        var documento = Documento.DeEmpresa(mundo.EmpresaId, tipo.Id, Hoy, VigenciaDocumento.VenceEl(Hoy.AddYears(5)));

        await mundo.Servicio().AgregarPendientesAsync(new AltasConAcreditacion { Documentos = [documento] });

        mundo.Agregadas.Should().BeEquivalentTo([(documento.Id, acceso.Id)]);
    }

    [Fact]
    public async Task Repetir_el_alta_no_duplica_acreditaciones()
    {
        var mundo = new MundoAcreditaciones();
        var trabajador = mundo.Trabajador();
        var centro = mundo.Centro();
        mundo.AccesoPlataforma(centro);
        mundo.Asignacion(trabajador, centro);
        var tipo = mundo.Tipo(RequisitoDocumental.Si);
        var documento = mundo.DocumentoDe(trabajador, tipo);
        var altas = new AltasConAcreditacion { Documentos = [documento] };

        (await mundo.Servicio().AgregarPendientesAsync(altas)).Should().Be(1);
        mundo.Guardar();

        (await mundo.Servicio().AgregarPendientesAsync(altas)).Should().Be(0);
        mundo.Acreditaciones.Acreditaciones.Should().BeEmpty();
        mundo.DocumentosContexto.ListaAcreditacionesDocumentoPlataforma.Should().ContainSingle();
    }

    [Fact]
    public async Task Dos_altas_del_mismo_guardado_que_llevan_al_mismo_par_lo_acreditan_una_sola_vez()
    {
        // El Documento nuevo y la Asignación nueva ponen en juego el mismo par
        // (Documento, acceso): el índice único haría fallar el SaveChanges.
        var mundo = new MundoAcreditaciones();
        var trabajador = mundo.Trabajador();
        var centro = mundo.Centro();
        mundo.AccesoPlataforma(centro);
        var tipo = mundo.Tipo(RequisitoDocumental.Si);
        var documento = DocumentoNuevoDe(trabajador, tipo);
        var asignacion = new Domain.Asignaciones.Asignacion(trabajador.Id, centro.Id, Hoy);

        var agregadas = await mundo.Servicio().AgregarPendientesAsync(
            new AltasConAcreditacion { Documentos = [documento], Asignaciones = [asignacion] });

        agregadas.Should().Be(1);
    }

    [Fact]
    public async Task Un_documento_de_cliente_no_se_acredita_ante_ninguna_plataforma()
    {
        var mundo = new MundoAcreditaciones();
        var trabajador = mundo.Trabajador();
        var centro = mundo.Centro();
        mundo.AccesoPlataforma(centro);
        mundo.Asignacion(trabajador, centro);
        var tipo = mundo.Tipo(RequisitoDocumental.Si, AmbitoAplicacion.Cliente, "Contrato marco");
        var documento = Documento.DeCliente(Guid.NewGuid(), tipo.Id, Hoy, VigenciaDocumento.VenceEl(Hoy.AddYears(5)));

        var agregadas = await mundo.Servicio().AgregarPendientesAsync(new AltasConAcreditacion { Documentos = [documento] });

        agregadas.Should().Be(0);
    }

    /// <summary>
    /// P1-X2 (frontera con P0-7): un Centro sin gestión CAE conserva sus accesos
    /// de plataforma, pero no exige nada, así que ni un Documento nuevo ni una
    /// Asignación nueva hacen nacer acreditaciones ante ellos.
    /// </summary>
    [Fact]
    public async Task Un_centro_sin_gestion_cae_no_recibe_acreditaciones_aunque_conserve_accesos_de_plataforma()
    {
        var mundo = new MundoAcreditaciones();
        var trabajador = mundo.Trabajador();
        var tipo = mundo.Tipo(RequisitoDocumental.Si);

        var centroConGestion = mundo.Centro("Planta Norte");
        var accesoConGestion = mundo.AccesoPlataforma(centroConGestion);
        mundo.Asignacion(trabajador, centroConGestion);

        var centroSinGestion = mundo.Centro("Almacén Sur");
        mundo.AccesoPlataforma(centroSinGestion);
        mundo.Asignacion(trabajador, centroSinGestion);
        centroSinGestion.EstablecerGestionCae(ModalidadGestionCae.SinGestionCae);

        var documento = DocumentoNuevoDe(trabajador, tipo);
        await mundo.Servicio().AgregarPendientesAsync(new AltasConAcreditacion { Documentos = [documento] });

        // Control positivo en el mismo alta: el Centro con gestión CAE sí la recibe.
        mundo.Agregadas.Should().BeEquivalentTo([(documento.Id, accesoConGestion.Id)]);

        // Camino de la Asignación: un Documento ya guardado y una Asignación nueva
        // al Centro sin gestión CAE tampoco acreditan.
        var otro = mundo.Trabajador("77189989B");
        mundo.DocumentoDe(otro, tipo);
        var agregadasPorAsignacion = await mundo.Servicio().AgregarPendientesAsync(new AltasConAcreditacion
        {
            Asignaciones = [new Domain.Asignaciones.Asignacion(otro.Id, centroSinGestion.Id, Hoy)]
        });

        agregadasPorAsignacion.Should().Be(0);
    }

    [Fact]
    public async Task Un_centro_que_el_alta_devuelve_a_gestion_cae_acredita_aunque_la_base_aun_lo_tenga_sin_gestion()
    {
        var mundo = new MundoAcreditaciones();
        var trabajador = mundo.Trabajador();
        var tipo = mundo.Tipo(RequisitoDocumental.Si);
        var centro = mundo.Centro("Almacén Sur");
        var acceso = mundo.AccesoPlataforma(centro);
        mundo.Asignacion(trabajador, centro);
        var documento = mundo.DocumentoDe(trabajador, tipo);
        // Lo que ve la consulta: el guardado que lo cambia todavía no ha ocurrido.
        centro.EstablecerGestionCae(ModalidadGestionCae.SinGestionCae);

        (await mundo.Servicio().AgregarPendientesAsync(new AltasConAcreditacion { Canales = [acceso] }))
            .Should().Be(0, "control: sin declararlo, el Centro sigue sin gestión CAE");

        await mundo.Servicio().AgregarPendientesAsync(new AltasConAcreditacion
        {
            Canales = [acceso],
            CentrosQueVuelvenAGestionCae = [centro.Id]
        });

        mundo.Agregadas.Should().BeEquivalentTo([(documento.Id, acceso.Id)]);
    }
}
