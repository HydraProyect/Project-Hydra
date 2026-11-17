using CaeManager.Domain.Documentos;
using CaeManager.Domain.Inbound.CatalogoBase;
using CaeManager.Domain.Inbound.Requisitos;
using CaeManager.Domain.Trabajadores;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Inbound.Requisitos;

/// <summary>
/// Invariantes de la plantilla de requisitos del Tenant TCE (B-14): una referencia por plantilla, requisitos que solo
/// cambian por la plantilla, origen que no exagera el nivel, D-15 en las exigencias del Cliente empresarial y R-6 en la
/// tolerancia. Los tipos de documento y requisitos de base son inventados.
/// </summary>
public class PlantillaRequisitosTests
{
    private static readonly AtributosRequisito Basicos = new(
        OrigenRequisito.PropioTenant, ModoVigenciaRequisito.LaPoneElDocumento, VigenciaMeses: null, ToleranciaDias: null,
        BloqueaAcceso: true, Criterio: "Firmado y vigente", DestinatarioRequisito.Todos, TiposContratacion.Todos, [],
        AmbitoDocumentoRequisito.GeneralTec);

    /// <summary>Con una base del catálogo, el origen puede ser de Actividad (D2: sin base solo propio o N3).</summary>
    private static readonly AtributosRequisito ConBase = Basicos with { Origen = OrigenRequisito.Actividad };

    private static TipoDocumento Tipo(
        AmbitoAplicacion ambito = AmbitoAplicacion.Trabajador, GrupoTipoDocumento? grupo = GrupoTipoDocumento.FormacionPrl,
        SensibilidadDocumental sensibilidad = SensibilidadDocumental.DatosPersonales)
    {
        var tipo = new TipoDocumento("Tipo de prueba", null, false, 0, ambito, sensibilidad: sensibilidad);
        tipo.EstablecerGrupo(grupo);
        return tipo;
    }

    private static PlantillaRequisitos DeCentro() => PlantillaRequisitos.ParaCentro(Guid.NewGuid());

    [Fact]
    public void Cada_fabrica_fija_su_tipo_y_una_sola_referencia()
    {
        var actividad = new ActividadCatalogo("actividad-prueba", "Actividad de prueba");
        var riesgo = RiesgoCatalogo.Nuevo("riesgo-prueba", "Riesgo de prueba");
        var centroId = Guid.NewGuid();
        var clienteId = Guid.NewGuid();

        var porActividad = PlantillaRequisitos.ParaActividad(actividad);
        var porRiesgo = PlantillaRequisitos.ParaRiesgo(riesgo);
        var deCentro = PlantillaRequisitos.ParaCentro(centroId);
        var exigencias = PlantillaRequisitos.ParaExigenciasDe(clienteId);

        porActividad.Should().Match<PlantillaRequisitos>(p => p.Tipo == TipoPlantillaRequisitos.PorActividad
            && p.ActividadId == actividad.Id && p.RiesgoId == null && p.CentroId == null && p.ClienteEmpresarialId == null && p.Activa);
        porRiesgo.Should().Match<PlantillaRequisitos>(p => p.Tipo == TipoPlantillaRequisitos.PorRiesgo
            && p.RiesgoId == riesgo.Id && p.ActividadId == null && p.CentroId == null && p.ClienteEmpresarialId == null);
        deCentro.Should().Match<PlantillaRequisitos>(p => p.Tipo == TipoPlantillaRequisitos.DeCentro
            && p.CentroId == centroId && p.ActividadId == null && p.RiesgoId == null && p.ClienteEmpresarialId == null);
        exigencias.Should().Match<PlantillaRequisitos>(p => p.Tipo == TipoPlantillaRequisitos.ExigenciasClienteEmpresarial
            && p.ClienteEmpresarialId == clienteId && p.ActividadId == null && p.RiesgoId == null && p.CentroId == null);
    }

    [Fact]
    public void Una_Actividad_o_un_riesgo_retirados_no_reciben_plantilla()
    {
        var actividad = new ActividadCatalogo("actividad-prueba", "Actividad de prueba");
        actividad.Retirar();
        var riesgo = RiesgoCatalogo.Nuevo("riesgo-prueba", "Riesgo de prueba");
        riesgo.Retirar();

        FluentActions.Invoking(() => PlantillaRequisitos.ParaActividad(actividad)).Should().Throw<InvalidOperationException>();
        FluentActions.Invoking(() => PlantillaRequisitos.ParaRiesgo(riesgo)).Should().Throw<InvalidOperationException>();
        FluentActions.Invoking(() => PlantillaRequisitos.ParaCentro(Guid.Empty)).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => PlantillaRequisitos.ParaExigenciasDe(Guid.Empty)).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Anadir_un_requisito_renueva_la_version_y_toma_la_entidad_del_tipo()
    {
        var plantilla = DeCentro();
        var version = plantilla.Version;
        var tipo = Tipo(AmbitoAplicacion.Empresa);

        var requisito = plantilla.AnadirRequisito(tipo, null, Basicos);

        plantilla.Version.Should().NotBe(version, "la fila del requisito vive en otra tabla y sin tocar la raíz no se vería el conflicto");
        requisito.Entidad.Should().Be(EntidadRequisito.Empresa);
        requisito.PlantillaId.Should().Be(plantilla.Id);
        requisito.TipoDocumentoId.Should().Be(tipo.Id);
        requisito.Activo.Should().BeTrue();
        plantilla.Requisitos.Should().ContainSingle();
    }

    [Theory]
    [InlineData(AmbitoAplicacion.Cliente)]
    [InlineData(AmbitoAplicacion.Proyecto)]
    public void Un_tipo_de_Cliente_o_de_Proyecto_no_es_un_requisito_de_acceso(AmbitoAplicacion ambito)
    {
        FluentActions.Invoking(() => DeCentro().AnadirRequisito(Tipo(ambito), null, Basicos)).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Un_tipo_archivado_no_se_pide()
    {
        var tipo = Tipo();
        tipo.Archivar(null);

        FluentActions.Invoking(() => DeCentro().AnadirRequisito(tipo, null, Basicos)).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Un_tipo_se_pide_una_sola_vez_mientras_su_requisito_este_activo()
    {
        var plantilla = DeCentro();
        var tipo = Tipo();
        var primero = plantilla.AnadirRequisito(tipo, null, Basicos);

        FluentActions.Invoking(() => plantilla.AnadirRequisito(tipo, null, Basicos)).Should().Throw<InvalidOperationException>();

        plantilla.RetirarRequisito(primero.Id);
        plantilla.AnadirRequisito(tipo, null, Basicos).Should().NotBeSameAs(primero);
        plantilla.Requisitos.Should().HaveCount(2);
    }

    [Fact]
    public void Una_plantilla_retirada_no_cambia()
    {
        var plantilla = DeCentro();
        var requisito = plantilla.AnadirRequisito(Tipo(), null, Basicos);
        plantilla.Retirar();

        plantilla.Activa.Should().BeFalse();
        FluentActions.Invoking(() => plantilla.AnadirRequisito(Tipo(), null, Basicos)).Should().Throw<InvalidOperationException>();
        FluentActions.Invoking(() => plantilla.RetirarRequisito(requisito.Id)).Should().Throw<InvalidOperationException>();
        FluentActions.Invoking(() => plantilla.EditarRequisito(requisito.Id, Tipo(), null, Basicos)).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Editar_no_cambia_el_tipo_ni_toca_un_requisito_retirado_ni_ajeno()
    {
        var plantilla = DeCentro();
        var tipo = Tipo();
        var requisito = plantilla.AnadirRequisito(tipo, null, Basicos);

        FluentActions.Invoking(() => plantilla.EditarRequisito(requisito.Id, Tipo(), null, Basicos)).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => plantilla.EditarRequisito(Guid.NewGuid(), tipo, null, Basicos)).Should().Throw<InvalidOperationException>();

        plantilla.EditarRequisito(requisito.Id, tipo, null, Basicos with { BloqueaAcceso = false, Criterio = "  Otro criterio  " });
        requisito.BloqueaAcceso.Should().BeFalse();
        requisito.Criterio.Should().Be("Otro criterio");

        plantilla.RetirarRequisito(requisito.Id);
        FluentActions.Invoking(() => plantilla.EditarRequisito(requisito.Id, tipo, null, Basicos)).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void La_vigencia_en_meses_va_con_la_vigencia_fija_y_solo_con_ella()
    {
        var plantilla = DeCentro();

        FluentActions.Invoking(() => plantilla.AnadirRequisito(Tipo(), null, Basicos with { ModoVigencia = ModoVigenciaRequisito.Fija }))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => plantilla.AnadirRequisito(Tipo(), null, Basicos with { VigenciaMeses = 12 }))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => plantilla.AnadirRequisito(Tipo(), null,
                Basicos with { ModoVigencia = ModoVigenciaRequisito.Fija, VigenciaMeses = AtributosRequisito.VigenciaMaximaMeses + 1 }))
            .Should().Throw<ArgumentOutOfRangeException>();

        plantilla.AnadirRequisito(Tipo(), null, Basicos with { ModoVigencia = ModoVigenciaRequisito.Fija, VigenciaMeses = 12 })
            .VigenciaMeses.Should().Be(12);
    }

    [Theory]
    [InlineData(GrupoTipoDocumento.VigilanciaDeLaSalud)]
    [InlineData(GrupoTipoDocumento.FormacionPrl)]
    [InlineData(GrupoTipoDocumento.SegurosYMutua)]
    public void R6_la_aptitud_medica_la_formacion_y_el_seguro_nunca_tienen_tolerancia(GrupoTipoDocumento grupo)
    {
        FluentActions.Invoking(() => DeCentro().AnadirRequisito(Tipo(grupo: grupo), null, Basicos with { ToleranciaDias = 15 }))
            .Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void R6_un_tipo_sin_grupo_o_de_salud_tampoco_tiene_tolerancia()
    {
        FluentActions.Invoking(() => DeCentro().AnadirRequisito(Tipo(grupo: null), null, Basicos with { ToleranciaDias = 15 }))
            .Should().Throw<InvalidOperationException>();
        FluentActions.Invoking(() => DeCentro().AnadirRequisito(
                Tipo(grupo: GrupoTipoDocumento.RelacionLaboral, sensibilidad: SensibilidadDocumental.CategoriaEspecialSalud), null,
                Basicos with { ToleranciaDias = 15 }))
            .Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void La_tolerancia_admitida_se_guarda_y_cero_es_sin_tolerancia()
    {
        var tipo = Tipo(grupo: GrupoTipoDocumento.RelacionLaboral);
        var plantilla = DeCentro();

        var conTolerancia = plantilla.AnadirRequisito(tipo, null, Basicos with { ToleranciaDias = 15 });
        conTolerancia.ToleranciaDias.Should().Be(15);
        conTolerancia.ToleranciaEfectiva(tipo).Should().Be(15);

        plantilla.EditarRequisito(conTolerancia.Id, tipo, null, Basicos with { ToleranciaDias = 0 });
        conTolerancia.ToleranciaDias.Should().BeNull();

        FluentActions.Invoking(() => plantilla.EditarRequisito(conTolerancia.Id, tipo, null,
                Basicos with { ToleranciaDias = 15, ModoVigencia = ModoVigenciaRequisito.SinVencimiento }))
            .Should().Throw<ArgumentException>("lo que no vence no tiene tolerancia");
        FluentActions.Invoking(() => plantilla.EditarRequisito(conTolerancia.Id, tipo, null,
                Basicos with { ToleranciaDias = AtributosRequisito.ToleranciaMaximaDias + 1 }))
            .Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void R6_la_tolerancia_efectiva_vuelve_a_mirar_el_grupo_del_tipo()
    {
        var tipo = Tipo(grupo: GrupoTipoDocumento.RelacionLaboral);
        var requisito = DeCentro().AnadirRequisito(tipo, null, Basicos with { ToleranciaDias = 30 });

        tipo.EstablecerGrupo(GrupoTipoDocumento.SegurosYMutua);

        requisito.ToleranciaDias.Should().Be(30, "lo fijado no se reescribe");
        requisito.ToleranciaEfectiva(tipo).Should().Be(0, "un seguro nunca tiene tolerancia (R-6)");
        FluentActions.Invoking(() => requisito.ToleranciaEfectiva(Tipo())).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void D15_en_las_exigencias_del_Cliente_empresarial_todo_es_exigencia_N3()
    {
        var plantilla = PlantillaRequisitos.ParaExigenciasDe(Guid.NewGuid());
        var baseN1 = new RequisitoBase("requisito-n1", "Con base N1", "Fundamento", NivelExigencia.N1);

        // Con una base N1 adoptada, D2 y la tabla de nivel admiten LegalN1 (control en una plantilla de Centro): lo que lo
        // rechaza aquí es solo la regla de la plantilla de exigencias.
        DeCentro().AnadirRequisito(Tipo(), baseN1, Basicos with { Origen = OrigenRequisito.LegalN1 })
            .Origen.Should().Be(OrigenRequisito.LegalN1);
        FluentActions.Invoking(() => plantilla.AnadirRequisito(Tipo(), baseN1, Basicos with { Origen = OrigenRequisito.LegalN1 }))
            .Should().Throw<ArgumentException>();
        plantilla.Requisitos.Should().BeEmpty();
        plantilla.AnadirRequisito(Tipo(), null, Basicos with { Origen = OrigenRequisito.ExigenciaN3 })
            .Origen.Should().Be(OrigenRequisito.ExigenciaN3);
    }

    [Theory]
    [InlineData(NivelExigencia.N3, OrigenRequisito.LegalN1)]
    [InlineData(NivelExigencia.N3, OrigenRequisito.Convenio)]
    [InlineData(NivelExigencia.N2, OrigenRequisito.LegalN1)]
    public void Adoptar_un_requisito_de_base_no_sube_su_nivel(NivelExigencia nivel, OrigenRequisito origen)
    {
        var requisitoBase = new RequisitoBase("requisito-prueba", "Requisito de prueba", "Fundamento de prueba", nivel);

        FluentActions.Invoking(() => DeCentro().AnadirRequisito(Tipo(), requisitoBase, Basicos with { Origen = origen }))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Un_requisito_de_base_retirado_o_pendiente_de_revision_legal_no_se_adopta_y_uno_propio_no_adopta_ninguno()
    {
        var retirado = new RequisitoBase("requisito-retirado", "Retirado", "Fundamento", NivelExigencia.N1);
        retirado.Retirar();
        var pendiente = new RequisitoBase("requisito-pendiente", "Pendiente", "Fundamento", NivelExigencia.N1, pendienteRevisionLegal: true);
        var vigente = new RequisitoBase("requisito-vigente", "Vigente", "Fundamento", NivelExigencia.N1);

        FluentActions.Invoking(() => DeCentro().AnadirRequisito(Tipo(), retirado, Basicos)).Should().Throw<InvalidOperationException>();
        FluentActions.Invoking(() => DeCentro().AnadirRequisito(Tipo(), pendiente, Basicos)).Should().Throw<InvalidOperationException>();
        FluentActions.Invoking(() => DeCentro().AnadirRequisito(Tipo(), vigente, Basicos with { Origen = OrigenRequisito.PropioTenant }))
            .Should().Throw<ArgumentException>();

        DeCentro().AnadirRequisito(Tipo(), vigente, Basicos with { Origen = OrigenRequisito.LegalN1 })
            .RequisitoBaseId.Should().Be(vigente.Id);
    }

    [Fact]
    public void Un_requisito_propio_no_se_convierte_en_legal_al_editarlo()
    {
        var plantilla = DeCentro();
        var tipo = Tipo();
        var propio = plantilla.AnadirRequisito(tipo, null, Basicos with { Origen = OrigenRequisito.PropioTenant });

        FluentActions.Invoking(() => plantilla.EditarRequisito(propio.Id, tipo, null, Basicos with { Origen = OrigenRequisito.LegalN1 }))
            .Should().Throw<ArgumentException>();
        propio.Origen.Should().Be(OrigenRequisito.PropioTenant);

        plantilla.EditarRequisito(propio.Id, tipo, null, Basicos with { Origen = OrigenRequisito.PropioTenant, BloqueaAcceso = false });
        propio.BloqueaAcceso.Should().BeFalse("el resto de atributos de uno propio sí se edita");
    }

    [Theory]
    [InlineData(OrigenRequisito.LegalN1)]
    [InlineData(OrigenRequisito.Convenio)]
    [InlineData(OrigenRequisito.Actividad)]
    public void D2_sin_base_un_origen_legal_de_convenio_o_de_Actividad_se_rechaza_al_anadir_y_al_editar(OrigenRequisito origen)
    {
        var plantilla = DeCentro();
        var tipo = Tipo();

        FluentActions.Invoking(() => plantilla.AnadirRequisito(tipo, null, Basicos with { Origen = origen }))
            .Should().Throw<ArgumentException>();
        plantilla.Requisitos.Should().BeEmpty();

        var propio = plantilla.AnadirRequisito(tipo, null, Basicos);
        FluentActions.Invoking(() => plantilla.EditarRequisito(propio.Id, tipo, null, Basicos with { Origen = origen }))
            .Should().Throw<ArgumentException>();
        propio.Origen.Should().Be(OrigenRequisito.PropioTenant);

        // Un requisito propio ya conserva su origen al editar: el camino que solo para D2 es una exigencia N3 sin base.
        var tipoN3 = Tipo();
        var exigencia = plantilla.AnadirRequisito(tipoN3, null, Basicos with { Origen = OrigenRequisito.ExigenciaN3 });
        FluentActions.Invoking(() => plantilla.EditarRequisito(exigencia.Id, tipoN3, null, Basicos with { Origen = origen }))
            .Should().Throw<ArgumentException>();
        exigencia.Origen.Should().Be(OrigenRequisito.ExigenciaN3, "sin base no sube a legal, de convenio o de Actividad");

        var requisitoBase = new RequisitoBase("requisito-n1", "Con base N1", "Fundamento", NivelExigencia.N1);
        DeCentro().AnadirRequisito(Tipo(), requisitoBase, Basicos with { Origen = origen }).Origen
            .Should().Be(origen, "adoptando una base del catálogo revisado sí se admite");
    }

    [Fact]
    public void D2_retirar_un_requisito_propio_y_volver_a_anadir_su_tipo_no_lo_convierte_en_legal()
    {
        var plantilla = DeCentro();
        var tipo = Tipo();
        var propio = plantilla.AnadirRequisito(tipo, null, Basicos);
        plantilla.RetirarRequisito(propio.Id);

        FluentActions.Invoking(() => plantilla.AnadirRequisito(tipo, null, Basicos with { Origen = OrigenRequisito.LegalN1 }))
            .Should().Throw<ArgumentException>();
        plantilla.Requisitos.Where(r => r.Activo).Should().BeEmpty();

        plantilla.AnadirRequisito(tipo, null, Basicos).Origen.Should().Be(OrigenRequisito.PropioTenant, "sigue pudiendo volver como propio");
    }

    [Fact]
    public void D2_sin_base_un_requisito_propio_o_una_exigencia_N3_se_admiten()
    {
        DeCentro().AnadirRequisito(Tipo(), null, Basicos).Origen.Should().Be(OrigenRequisito.PropioTenant);
        PlantillaRequisitos.ParaExigenciasDe(Guid.NewGuid()).AnadirRequisito(Tipo(), null, Basicos with { Origen = OrigenRequisito.ExigenciaN3 })
            .Origen.Should().Be(OrigenRequisito.ExigenciaN3);
    }

    [Fact]
    public void Editar_no_desadopta_el_requisito_de_base_ni_sube_su_origen()
    {
        var plantilla = DeCentro();
        var tipo = Tipo();
        var n3 = new RequisitoBase("requisito-n3", "Exigencia N3", "Fundamento", NivelExigencia.N3);
        var otro = new RequisitoBase("requisito-otro", "Otro", "Fundamento", NivelExigencia.N1);
        var requisito = plantilla.AnadirRequisito(tipo, n3, ConBase);

        FluentActions.Invoking(() => plantilla.EditarRequisito(requisito.Id, tipo, null, Basicos with { Origen = OrigenRequisito.LegalN1 }))
            .Should().Throw<ArgumentException>("omitir la base la desadoptaría y saltaría la guarda de nivel");
        FluentActions.Invoking(() => plantilla.EditarRequisito(requisito.Id, tipo, otro, Basicos with { Origen = OrigenRequisito.LegalN1 }))
            .Should().Throw<ArgumentException>("cambiar de base es otro requisito");
        requisito.RequisitoBaseId.Should().Be(n3.Id);
        requisito.Origen.Should().Be(OrigenRequisito.Actividad, "la edición rechazada no lo cambia");
    }

    [Fact]
    public void Un_requisito_que_adopto_una_base_retirada_despues_sigue_editandose()
    {
        var plantilla = DeCentro();
        var tipo = Tipo();
        var requisitoBase = new RequisitoBase("requisito-luego-retirado", "Luego retirado", "Fundamento", NivelExigencia.N1);
        var requisito = plantilla.AnadirRequisito(tipo, requisitoBase, ConBase);
        requisitoBase.Retirar();

        plantilla.EditarRequisito(requisito.Id, tipo, requisitoBase, ConBase with { Criterio = "Criterio nuevo" });

        requisito.Criterio.Should().Be("Criterio nuevo");
        FluentActions.Invoking(() => DeCentro().AnadirRequisito(tipo, requisitoBase, ConBase))
            .Should().Throw<InvalidOperationException>("adoptarla de nuevo sí exige que siga activa");
    }

    [Fact]
    public void A_quien_se_pide_tiene_sentido_para_el_sujeto()
    {
        var vehiculo = Tipo(AmbitoAplicacion.Vehiculo, grupo: null);
        var empresa = Tipo(AmbitoAplicacion.Empresa, grupo: null);

        FluentActions.Invoking(() => DeCentro().AnadirRequisito(vehiculo, null, Basicos with { SePideA = DestinatarioRequisito.Extranjeros }))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => DeCentro().AnadirRequisito(empresa, null, Basicos with { TiposContratacion = TiposContratacion.Autonomo }))
            .Should().Throw<ArgumentException>("el tipo de contratación solo acota Trabajadores");
        FluentActions.Invoking(() => DeCentro().AnadirRequisito(Tipo(), null, Basicos with { TiposContratacion = TiposContratacion.Ninguno }))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => DeCentro().AnadirRequisito(Tipo(), null, Basicos with { TiposContratacion = (TiposContratacion)64 }))
            .Should().Throw<ArgumentException>();

        DeCentro().AnadirRequisito(Tipo(), null, Basicos with { TiposContratacion = TiposContratacion.Autonomo })
            .TiposContratacion.Should().Be(TiposContratacion.Autonomo);
    }

    [Fact]
    public void Un_requisito_de_Trabajador_se_puede_acotar_al_Extranjero_y_una_Empresa_exige_Todos_con_el_Extranjero_dentro()
    {
        var requisito = DeCentro().AnadirRequisito(Tipo(), null, Basicos with { TiposContratacion = TiposContratacion.Extranjero });

        requisito.TiposContratacion.Should().Be(TiposContratacion.Extranjero);
        requisito.TiposContratacion.Incluye(TipoContratacion.Extranjero).Should().BeTrue();
        requisito.TiposContratacion.Incluye(TipoContratacion.CuentaAjena).Should().BeFalse();

        var empresa = Tipo(AmbitoAplicacion.Empresa, grupo: null);
        FluentActions.Invoking(() => DeCentro().AnadirRequisito(empresa, null, Basicos with
            {
                TiposContratacion = TiposContratacion.CuentaAjena | TiposContratacion.Autonomo | TiposContratacion.Voluntario,
            }))
            .Should().Throw<ArgumentException>("el Todos de antes (7) ya no es Todos: le falta el Extranjero");
        DeCentro().AnadirRequisito(empresa, null, Basicos).TiposContratacion.Should().Be(TiposContratacion.Todos);
    }

    [Fact]
    public void Las_equivalencias_se_limpian_y_no_valen_si_solo_se_pide_a_nacionales()
    {
        var requisito = DeCentro().AnadirRequisito(Tipo(), null,
            Basicos with { SePideA = DestinatarioRequisito.Extranjeros, Equivalencias = ["  Certificado del país de origen ", "certificado del país de origen", " "] });

        requisito.Equivalencias.Should().Equal("Certificado del país de origen");

        FluentActions.Invoking(() => DeCentro().AnadirRequisito(Tipo(), null,
                Basicos with { SePideA = DestinatarioRequisito.Nacionales, Equivalencias = ["Algo"] }))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => DeCentro().AnadirRequisito(Tipo(), null,
                Basicos with { Equivalencias = Enumerable.Range(0, AtributosRequisito.MaximoEquivalencias + 1).Select(i => $"E{i}").ToList() }))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => DeCentro().AnadirRequisito(Tipo(), null,
                Basicos with { Equivalencias = [new string('x', AtributosRequisito.LongitudMaximaEquivalencia + 1)] }))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void El_criterio_tiene_un_maximo_y_uno_en_blanco_es_ninguno()
    {
        DeCentro().AnadirRequisito(Tipo(), null, Basicos with { Criterio = "   " }).Criterio.Should().BeNull();
        FluentActions.Invoking(() => DeCentro().AnadirRequisito(Tipo(), null,
                Basicos with { Criterio = new string('x', AtributosRequisito.LongitudMaximaCriterio + 1) }))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Un_valor_fuera_del_catalogo_se_rechaza()
    {
        FluentActions.Invoking(() => DeCentro().AnadirRequisito(Tipo(), null, Basicos with { Origen = (OrigenRequisito)99 }))
            .Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => DeCentro().AnadirRequisito(Tipo(), null, Basicos with { AmbitoDocumento = (AmbitoDocumentoRequisito)0 }))
            .Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Retirar_es_idempotente_y_solo_renueva_la_version_si_cambia()
    {
        var plantilla = DeCentro();
        plantilla.Retirar();
        var version = plantilla.Version;

        plantilla.Retirar();

        plantilla.Version.Should().Be(version);
    }
}
