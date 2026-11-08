using CaeManager.Application.Documentos.Acreditacion;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Trabajadores;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Documentos;

/// <summary>
/// Regla única de alta de acreditaciones de plataforma (P0-7): un Documento se
/// acredita ante cada acceso de plataforma de cada Centro donde su propietario
/// tiene actividad (el Trabajador asignado; la Empresa, en sus Centros y donde
/// tenga Trabajadores asignados) y que exige su tipo; nunca ante un Centro que no lo exige; nunca dos
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

    // A-1, decisión del propietario del producto del 2026-11-08: un Documento de Empresa se acredita en todos los Centros
    // de esa Empresa (Centro.EmpresaId) gestionados por plataforma que exigen su tipo, tengan o no Trabajadores asignados.
    // El acceso por correo nunca acredita.

    private static Documento DocumentoNuevoDeEmpresa(MundoAcreditaciones mundo, TipoDocumento tipo) =>
        Documento.DeEmpresa(mundo.EmpresaId, tipo.Id, Hoy, VigenciaDocumento.VenceEl(Hoy.AddYears(5)));

    [Fact]
    public async Task Un_documento_de_empresa_nuevo_se_acredita_en_un_centro_de_plataforma_de_su_empresa_sin_trabajadores_asignados()
    {
        var mundo = new MundoAcreditaciones();
        var centro = mundo.Centro("Planta sin personal", mundo.EmpresaId);
        var acceso = mundo.AccesoPlataforma(centro);
        var tipo = mundo.Tipo(RequisitoDocumental.Si, AmbitoAplicacion.Empresa, "Seguro RC");
        var documento = DocumentoNuevoDeEmpresa(mundo, tipo);

        var agregadas = await mundo.Servicio().AgregarPendientesAsync(new AltasConAcreditacion { Documentos = [documento] });

        agregadas.Should().Be(1);
        mundo.Agregadas.Should().BeEquivalentTo([(documento.Id, acceso.Id)],
            "el Centro es de la Empresa y la exige al día en su plataforma aunque nadie esté asignado allí");
    }

    [Fact]
    public async Task Un_documento_de_empresa_no_se_acredita_en_un_centro_de_su_empresa_gestionado_por_correo()
    {
        var mundo = new MundoAcreditaciones();
        var centroPorCorreo = mundo.Centro("Planta por correo", mundo.EmpresaId);
        mundo.AccesoCorreo(centroPorCorreo);
        var centroConPlataforma = mundo.Centro("Planta con plataforma", mundo.EmpresaId);
        var acceso = mundo.AccesoPlataforma(centroConPlataforma);
        var tipo = mundo.Tipo(RequisitoDocumental.Si, AmbitoAplicacion.Empresa, "Seguro RC");
        var documento = DocumentoNuevoDeEmpresa(mundo, tipo);

        await mundo.Servicio().AgregarPendientesAsync(new AltasConAcreditacion { Documentos = [documento] });

        mundo.Agregadas.Should().BeEquivalentTo([(documento.Id, acceso.Id)],
            "el correo no se actualiza hasta que se programa una Visita; solo la plataforma (control positivo) acredita");
    }

    [Fact]
    public async Task Un_documento_de_empresa_no_se_acredita_en_un_centro_de_otra_empresa_sin_trabajadores_suyos()
    {
        var mundo = new MundoAcreditaciones();
        var otraEmpresaId = Guid.NewGuid();
        var centroAjeno = mundo.Centro("Planta de otra Empresa", otraEmpresaId);
        var accesoAjeno = mundo.AccesoPlataforma(centroAjeno);
        var centroPropio = mundo.Centro("Planta propia", mundo.EmpresaId);
        var acceso = mundo.AccesoPlataforma(centroPropio);
        var tipo = mundo.Tipo(RequisitoDocumental.Si, AmbitoAplicacion.Empresa, "Seguro RC");
        var documento = DocumentoNuevoDeEmpresa(mundo, tipo);
        // En el mismo alta (una importación), el de la otra Empresa: cada uno solo llega a los Centros de la suya.
        var documentoAjeno = Documento.DeEmpresa(otraEmpresaId, tipo.Id, Hoy, VigenciaDocumento.VenceEl(Hoy.AddYears(5)));

        await mundo.Servicio().AgregarPendientesAsync(new AltasConAcreditacion { Documentos = [documento, documentoAjeno] });

        mundo.Agregadas.Should().BeEquivalentTo([(documento.Id, acceso.Id), (documentoAjeno.Id, accesoAjeno.Id)]);
    }

    [Fact]
    public async Task Un_documento_de_empresa_no_se_acredita_en_un_centro_de_su_empresa_que_no_exige_el_tipo()
    {
        var mundo = new MundoAcreditaciones();
        var tipo = mundo.Tipo(RequisitoDocumental.No, AmbitoAplicacion.Empresa, "ISO 9001");
        var centroQueLoExige = mundo.Centro("Planta que lo exige", mundo.EmpresaId);
        var acceso = mundo.AccesoPlataforma(centroQueLoExige);
        mundo.Requisito(tipo, centroQueLoExige, incluido: true);
        // Sin fila y con el tipo no obligatorio por defecto: no lo exige.
        var centroSinFila = mundo.Centro("Planta sin fila", mundo.EmpresaId);
        mundo.AccesoPlataforma(centroSinFila);
        var documento = DocumentoNuevoDeEmpresa(mundo, tipo);

        await mundo.Servicio().AgregarPendientesAsync(new AltasConAcreditacion { Documentos = [documento] });

        mundo.Agregadas.Should().BeEquivalentTo([(documento.Id, acceso.Id)]);
    }

    [Fact]
    public async Task Un_documento_de_trabajador_sigue_sin_acreditarse_en_un_centro_de_su_empresa_donde_no_esta_asignado()
    {
        var mundo = new MundoAcreditaciones();
        var trabajador = mundo.Trabajador();
        var centroSinAsignar = mundo.Centro("Planta sin asignar", mundo.EmpresaId);
        mundo.AccesoPlataforma(centroSinAsignar);
        var centroAsignado = mundo.Centro("Planta asignada", mundo.EmpresaId);
        var acceso = mundo.AccesoPlataforma(centroAsignado);
        mundo.Asignacion(trabajador, centroAsignado);
        var tipo = mundo.Tipo(RequisitoDocumental.Si);
        var documento = DocumentoNuevoDe(trabajador, tipo);

        await mundo.Servicio().AgregarPendientesAsync(new AltasConAcreditacion { Documentos = [documento] });

        mundo.Agregadas.Should().BeEquivalentTo([(documento.Id, acceso.Id)],
            "un Documento de Trabajador solo cuenta donde el Trabajador tiene una Asignación activa");
    }

    [Fact]
    public async Task Un_acceso_de_plataforma_nuevo_en_un_centro_de_la_empresa_sin_trabajadores_acredita_sus_documentos_de_empresa()
    {
        var mundo = new MundoAcreditaciones();
        var tipoEmpresa = mundo.Tipo(RequisitoDocumental.Si, AmbitoAplicacion.Empresa, "Seguro RC");
        var tipoTrabajador = mundo.Tipo(RequisitoDocumental.Si);
        var deEmpresa = mundo.DocumentoDeEmpresa(tipoEmpresa);
        mundo.DocumentoDe(mundo.Trabajador(), tipoTrabajador);
        var centro = mundo.Centro("Planta sin personal", mundo.EmpresaId);
        var acceso = mundo.AccesoPlataforma(centro);
        var centroAjeno = mundo.Centro("Planta de otra Empresa");
        var accesoAjeno = mundo.AccesoPlataforma(centroAjeno);

        await mundo.Servicio().AgregarPendientesAsync(new AltasConAcreditacion { Canales = [acceso, accesoAjeno] });

        mundo.Agregadas.Should().BeEquivalentTo([(deEmpresa.Id, acceso.Id)],
            "el Documento de Trabajador sin Asignación allí y el Centro de otra Empresa no acreditan");
    }

    [Fact]
    public async Task Un_tipo_que_un_centro_de_la_empresa_sin_trabajadores_pasa_a_exigir_acredita_sus_documentos_de_empresa()
    {
        var mundo = new MundoAcreditaciones();
        var tipo = mundo.Tipo(RequisitoDocumental.No, AmbitoAplicacion.Empresa, "ISO 9001");
        var deEmpresa = mundo.DocumentoDeEmpresa(tipo);
        var centro = mundo.Centro("Planta sin personal", mundo.EmpresaId);
        var acceso = mundo.AccesoPlataforma(centro);

        await mundo.Servicio().AgregarPendientesAsync(new AltasConAcreditacion
        {
            Requisitos = [new TipoDocumentoCentro(tipo.Id, centro.Id, incluido: true)]
        });

        mundo.Agregadas.Should().BeEquivalentTo([(deEmpresa.Id, acceso.Id)]);
    }

    [Fact]
    public async Task Un_documento_de_empresa_suma_sus_centros_a_los_de_sus_trabajadores_asignados_sin_quitar_ninguno()
    {
        var mundo = new MundoAcreditaciones();
        var centroPropio = mundo.Centro("Planta propia sin personal", mundo.EmpresaId);
        var accesoPropio = mundo.AccesoPlataforma(centroPropio);
        // Centro de otra Empresa donde trabaja alguien de esta: la vía anterior sigue valiendo.
        var centroAjeno = mundo.Centro("Planta de otra Empresa");
        var accesoAjeno = mundo.AccesoPlataforma(centroAjeno);
        mundo.Asignacion(mundo.Trabajador(), centroAjeno);
        var tipo = mundo.Tipo(RequisitoDocumental.Si, AmbitoAplicacion.Empresa, "Seguro RC");
        var documento = DocumentoNuevoDeEmpresa(mundo, tipo);

        await mundo.Servicio().AgregarPendientesAsync(new AltasConAcreditacion { Documentos = [documento] });

        mundo.Agregadas.Should().BeEquivalentTo([(documento.Id, accesoPropio.Id), (documento.Id, accesoAjeno.Id)]);
    }

    [Fact]
    public async Task Repetir_el_alta_de_un_documento_de_empresa_no_duplica_acreditaciones()
    {
        var mundo = new MundoAcreditaciones();
        var centro = mundo.Centro("Planta sin personal", mundo.EmpresaId);
        mundo.AccesoPlataforma(centro);
        var tipo = mundo.Tipo(RequisitoDocumental.Si, AmbitoAplicacion.Empresa, "Seguro RC");
        var documento = mundo.DocumentoDeEmpresa(tipo);
        var altas = new AltasConAcreditacion { Documentos = [documento] };

        (await mundo.Servicio().AgregarPendientesAsync(altas)).Should().Be(1);
        mundo.Guardar();

        (await mundo.Servicio().AgregarPendientesAsync(altas)).Should().Be(0);
        (await mundo.Servicio().AgregarPendientesAsync(new AltasConAcreditacion
        {
            Canales = mundo.CentrosContexto.ListaCanalesGestionDocumental.ToList()
        })).Should().Be(0, "el acceso ya acreditado no vuelve a acreditar el mismo Documento");
        mundo.DocumentosContexto.ListaAcreditacionesDocumentoPlataforma.Should().ContainSingle();
    }

    [Fact]
    public async Task Un_centro_de_la_empresa_sin_trabajadores_que_vuelve_a_gestion_cae_acredita_sus_documentos_de_empresa()
    {
        var mundo = new MundoAcreditaciones();
        var centro = mundo.Centro("Almacén sin personal", mundo.EmpresaId);
        var acceso = mundo.AccesoPlataforma(centro);
        var tipo = mundo.Tipo(RequisitoDocumental.Si, AmbitoAplicacion.Empresa, "Seguro RC");
        var documento = mundo.DocumentoDeEmpresa(tipo);
        // Lo que ve la consulta: el guardado que lo devuelve a gestión CAE todavía no ha ocurrido.
        centro.EstablecerGestionCae(ModalidadGestionCae.SinGestionCae);

        await mundo.Servicio().AgregarPendientesAsync(new AltasConAcreditacion
        {
            Canales = [acceso],
            CentrosQueVuelvenAGestionCae = [centro.Id]
        });

        mundo.Agregadas.Should().BeEquivalentTo([(documento.Id, acceso.Id)]);
    }

    [Fact]
    public async Task Un_centro_de_la_empresa_sin_gestion_cae_no_recibe_sus_documentos_de_empresa()
    {
        var mundo = new MundoAcreditaciones();
        var centro = mundo.Centro("Almacén sin gestión", mundo.EmpresaId);
        mundo.AccesoPlataforma(centro);
        centro.EstablecerGestionCae(ModalidadGestionCae.SinGestionCae);
        var tipo = mundo.Tipo(RequisitoDocumental.Si, AmbitoAplicacion.Empresa, "Seguro RC");

        var agregadas = await mundo.Servicio().AgregarPendientesAsync(
            new AltasConAcreditacion { Documentos = [DocumentoNuevoDeEmpresa(mundo, tipo)] });

        agregadas.Should().Be(0);
    }

    [Fact]
    public async Task Un_tipo_que_pasa_a_exigirse_de_serie_acredita_en_cada_centro_de_plataforma_sin_fila_aunque_la_base_aun_no_lo_exija()
    {
        var mundo = new MundoAcreditaciones();
        // Guardado como no obligatorio: el alta que lo cambia prevalece sobre la base.
        var tipo = mundo.Tipo(RequisitoDocumental.No, AmbitoAplicacion.Empresa, "Seguro RC");
        var deEmpresa = mundo.DocumentoDeEmpresa(tipo);
        var centroSinFila = mundo.Centro("Planta sin personal", mundo.EmpresaId);
        var acceso = mundo.AccesoPlataforma(centroSinFila);
        var centroQueLoExcluye = mundo.Centro("Planta que lo excluye", mundo.EmpresaId);
        mundo.AccesoPlataforma(centroQueLoExcluye);
        mundo.Requisito(tipo, centroQueLoExcluye, incluido: false);
        var centroPorCorreo = mundo.Centro("Planta por correo", mundo.EmpresaId);
        mundo.AccesoCorreo(centroPorCorreo);
        var centroAjeno = mundo.Centro("Planta de otra Empresa");
        mundo.AccesoPlataforma(centroAjeno);

        var agregadas = await mundo.Servicio().AgregarPendientesAsync(
            new AltasConAcreditacion { TiposQuePasanAExigirseDeSerie = [tipo.Id] });

        agregadas.Should().Be(1);
        mundo.Agregadas.Should().BeEquivalentTo([(deEmpresa.Id, acceso.Id)],
            "la fila explícita sigue mandando, el correo no acredita y el Centro de otra Empresa no tiene actividad suya");
    }

    [Fact]
    public async Task Un_tipo_que_pasa_a_exigirse_de_serie_solo_pone_en_juego_ese_tipo()
    {
        var mundo = new MundoAcreditaciones();
        var tipo = mundo.Tipo(RequisitoDocumental.No, AmbitoAplicacion.Empresa, "Seguro RC");
        var otroTipo = mundo.Tipo(RequisitoDocumental.Si, AmbitoAplicacion.Empresa, "Recibo del seguro");
        var deEmpresa = mundo.DocumentoDeEmpresa(tipo);
        mundo.DocumentoDeEmpresa(otroTipo);
        var acceso = mundo.AccesoPlataforma(mundo.Centro("Planta sin personal", mundo.EmpresaId));

        await mundo.Servicio().AgregarPendientesAsync(new AltasConAcreditacion { TiposQuePasanAExigirseDeSerie = [tipo.Id] });

        mundo.Agregadas.Should().BeEquivalentTo([(deEmpresa.Id, acceso.Id)],
            "el otro tipo, aunque falte su acreditación, no es lo que este alta pone en juego");
    }

    [Fact]
    public async Task Un_tipo_que_pasa_a_exigirse_de_serie_no_acredita_los_documentos_de_trabajador()
    {
        // Revisión Codex de la PR #2074, ronda 5: antes de la regla A-1, pasar Requerido a Sí no acreditaba nada, y para
        // los Documentos de Trabajador sigue igual (decisión del propietario del producto del 2026-11-08). El Trabajador
        // está asignado a un Centro de plataforma que pasa a exigir su tipo; el Documento de Empresa del mismo Centro sí
        // se acredita (control: la propagación recorrió ese Centro, y lo único que deja fuera al de Trabajador es su
        // propietario, no la falta de actividad ni de exigencia).
        var mundo = new MundoAcreditaciones();
        var tipoTrabajador = mundo.Tipo(RequisitoDocumental.No);
        var tipoEmpresa = mundo.Tipo(RequisitoDocumental.No, AmbitoAplicacion.Empresa, "Seguro RC");
        var centro = mundo.Centro("Planta", mundo.EmpresaId);
        var acceso = mundo.AccesoPlataforma(centro);
        var trabajador = mundo.Trabajador();
        mundo.Asignacion(trabajador, centro);
        mundo.DocumentoDe(trabajador, tipoTrabajador);
        var deEmpresa = mundo.DocumentoDeEmpresa(tipoEmpresa);

        var agregadas = await mundo.Servicio().AgregarPendientesAsync(
            new AltasConAcreditacion { TiposQuePasanAExigirseDeSerie = [tipoTrabajador.Id, tipoEmpresa.Id] });

        agregadas.Should().Be(1);
        mundo.Agregadas.Should().BeEquivalentTo([(deEmpresa.Id, acceso.Id)],
            "el valor general que pasa a Sí solo propaga los Documentos de Empresa");
    }

    [Fact]
    public async Task Un_tipo_que_pasa_a_exigirse_de_serie_no_acredita_un_documento_de_trabajador_que_el_mismo_alta_pone_en_juego_por_otra_via()
    {
        // La misma edición exige el tipo en un Centro concreto (fila nueva) y lo pasa a exigir de serie. El Trabajador
        // está asignado a los dos Centros: se acredita donde la fila nueva lo exige, como en origin/main, y no en el
        // Centro sin fila al que solo llega el valor general, aunque su Documento ya esté cargado por la otra vía.
        var mundo = new MundoAcreditaciones();
        var tipo = mundo.Tipo(RequisitoDocumental.No);
        var centroConFila = mundo.Centro("Planta con fila");
        var accesoConFila = mundo.AccesoPlataforma(centroConFila);
        var centroSinFila = mundo.Centro("Planta sin fila");
        mundo.AccesoPlataforma(centroSinFila);
        var trabajador = mundo.Trabajador();
        mundo.Asignacion(trabajador, centroConFila);
        mundo.Asignacion(trabajador, centroSinFila);
        var documento = mundo.DocumentoDe(trabajador, tipo);

        await mundo.Servicio().AgregarPendientesAsync(new AltasConAcreditacion
        {
            Requisitos = [new TipoDocumentoCentro(tipo.Id, centroConFila.Id)],
            TiposQuePasanAExigirseDeSerie = [tipo.Id],
        });

        mundo.Agregadas.Should().BeEquivalentTo([(documento.Id, accesoConFila.Id)],
            "la fila nueva acredita al Trabajador como antes; el valor general no lo propaga al Centro sin fila");
    }

    [Fact]
    public async Task Conciliar_un_centro_crea_solo_las_acreditaciones_que_faltan_de_sus_documentos_de_empresa()
    {
        var mundo = new MundoAcreditaciones();
        var tipoEmpresa = mundo.Tipo(RequisitoDocumental.Si, AmbitoAplicacion.Empresa, "Seguro RC");
        var tipoTrabajador = mundo.Tipo(RequisitoDocumental.Si);
        var deEmpresa = mundo.DocumentoDeEmpresa(tipoEmpresa);
        var centro = mundo.Centro("Planta", mundo.EmpresaId);
        var accesoYaAcreditado = mundo.AccesoPlataforma(centro);
        var accesoSinAcreditar = mundo.AccesoPlataforma(centro);
        mundo.DocumentosContexto.ListaAcreditacionesDocumentoPlataforma.Add(
            new AcreditacionDocumentoPlataforma(deEmpresa.Id, accesoYaAcreditado.Id));
        // Un Documento de Trabajador asignado allí y sin acreditar: su regla no cambió, la conciliación no lo toca.
        var trabajador = mundo.Trabajador();
        mundo.Asignacion(trabajador, centro);
        mundo.DocumentoDe(trabajador, tipoTrabajador);

        var agregadas = await mundo.Servicio().AgregarPendientesAsync(
            new AltasConAcreditacion { CentrosAConciliar = [centro.Id], SoloDocumentosDeEmpresa = true });

        agregadas.Should().Be(1);
        mundo.Agregadas.Should().BeEquivalentTo([(deEmpresa.Id, accesoSinAcreditar.Id)]);
    }

    [Fact]
    public async Task Conciliar_un_centro_sin_restringir_a_empresa_tambien_alcanzaria_los_documentos_de_trabajador()
    {
        // Control del instrumento del test anterior: el Documento de Trabajador sí es candidato, y lo que lo deja
        // fuera es SoloDocumentosDeEmpresa, no la falta de actividad.
        var mundo = new MundoAcreditaciones();
        var tipoTrabajador = mundo.Tipo(RequisitoDocumental.Si);
        var centro = mundo.Centro("Planta", mundo.EmpresaId);
        var acceso = mundo.AccesoPlataforma(centro);
        var trabajador = mundo.Trabajador();
        mundo.Asignacion(trabajador, centro);
        var deTrabajador = mundo.DocumentoDe(trabajador, tipoTrabajador);

        await mundo.Servicio().AgregarPendientesAsync(new AltasConAcreditacion { CentrosAConciliar = [centro.Id] });

        mundo.Agregadas.Should().BeEquivalentTo([(deTrabajador.Id, acceso.Id)]);
    }
}
