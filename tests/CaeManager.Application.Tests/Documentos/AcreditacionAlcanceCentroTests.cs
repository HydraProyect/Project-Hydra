using CaeManager.Application.Documentos.Commands.ConfirmarSubidaDesdeExtension;
using CaeManager.Application.Documentos.Commands.ConfirmarVigenciaAcreditacion;
using CaeManager.Application.Documentos.Commands.MarcarAcreditacionAceptada;
using CaeManager.Application.Documentos.Commands.MarcarAcreditacionPendienteDeSubir;
using CaeManager.Application.Documentos.Commands.MarcarAcreditacionRechazada;
using CaeManager.Application.Documentos.Commands.MarcarAcreditacionSubida;
using CaeManager.Application.Documentos.Commands.RestaurarAnotacionAcreditacion;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Tests.Proyectos;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Integraciones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Documentos;

public class AcreditacionAlcanceCentroTests
{
    [Theory]
    [InlineData("subida")]
    [InlineData("confirmar")]
    [InlineData("deshacer")]
    [InlineData("aceptada")]
    [InlineData("rechazada")]
    [InlineData("pendiente")]
    [InlineData("vigencia")]
    [InlineData("restaurar")]
    public async Task Documento_visible_exige_centro_de_gestion_y_admite_cartera_total(string operacion)
    {
        foreach (var alcanceCentro in new[] { "fuera", "dentro", "total" })
        {
            var centroId = Guid.NewGuid();
            var documento = Documento.DeTrabajador(Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 1, 1), VigenciaDocumento.NoCaduca);
            var proveedor = new ProveedorPlataformaCae("dokify", "Dokify");
            var canal = CanalGestionDocumental.DePlataforma(centroId, "Portal", proveedor.Id, null, null, null);
            var acreditacion = new AcreditacionDocumentoPlataforma(documento.Id, canal.Id);
            var usuarioId = Guid.NewGuid();
            var cambio = operacion == "deshacer"
                ? acreditacion.ConfirmarEnvioDesdeExtension(new DateOnly(2026, 1, 2), usuarioId, DateTime.UtcNow)
                : null;
            if (operacion == "pendiente") acreditacion.MarcarSubida();
            // Restaurar deshace una anotación: solo devuelve el estado previo desde Aceptada.
            if (operacion == "restaurar") acreditacion.MarcarAceptada(VigenciaEnPlataforma.NoVenceAqui);
            var documentos = new DocumentoRepositorioFalso();
            documentos.Agregar(documento);
            var acreditaciones = new AcreditacionDocumentoPlataformaRepositorioFalso();
            acreditaciones.Agregar(acreditacion);
            var centros = new CentrosQueryContextFalso();
            centros.ListaCanalesGestionDocumental.Add(canal);
            var proveedores = new ProveedoresPlataformaCaeQueryContextFalso();
            proveedores.ListaProveedores.Add(proveedor);
            proveedores.ListaDominios.Add(new DominioProveedorPlataformaCae(proveedor.Id, "dokify.net"));
            var alcance = new AlcanceDatosServiceFalso(centroIdsParaGestion: alcanceCentro switch
            {
                "fuera" => [Guid.NewGuid()],
                "dentro" => [centroId],
                _ => null
            });
            var proyectos = new ProyectosQueryContextFalso();
            var unit = new UnitOfWorkFalso();
            var version = acreditacion.Version;
            Result resultado = operacion switch
            {
                "subida" => await new MarcarAcreditacionSubidaCommandHandler(acreditaciones, documentos, alcance, proyectos, centros, unit)
                    .Handle(new(acreditacion.Id), CancellationToken.None),
                "confirmar" => await new ConfirmarSubidaDesdeExtensionCommandHandler(acreditaciones, documentos, alcance, proyectos, centros, proveedores, new CurrentUserServiceFalso(usuarioId), unit)
                    .Handle(new(acreditacion.Id, "app.dokify.net"), CancellationToken.None),
                "deshacer" => await new DeshacerSubidaDesdeExtensionCommandHandler(acreditaciones, documentos, alcance, proyectos, centros, unit)
                    .Handle(new(acreditacion.Id, cambio!.Id, version), CancellationToken.None),
                "aceptada" => await new MarcarAcreditacionAceptadaCommandHandler(acreditaciones, documentos, alcance, proyectos, centros, unit)
                    .Handle(new(acreditacion.Id, VigenciaEnPlataforma.NoVenceAqui), CancellationToken.None),
                "rechazada" => await new MarcarAcreditacionRechazadaCommandHandler(acreditaciones, documentos, alcance, proyectos, centros, unit)
                    .Handle(new(acreditacion.Id, CausaRechazoAcreditacion.Ilegible, "Firma ilegible"), CancellationToken.None),
                "pendiente" => await new MarcarAcreditacionPendienteDeSubirCommandHandler(acreditaciones, documentos, alcance, proyectos, centros, unit)
                    .Handle(new(acreditacion.Id), CancellationToken.None),
                "vigencia" => await new ConfirmarVigenciaAcreditacionCommandHandler(acreditaciones, documentos, alcance, proyectos, centros, unit)
                    .Handle(new(acreditacion.Id, VigenciaEnPlataforma.NoVenceAqui), CancellationToken.None),
                _ => await new RestaurarAnotacionAcreditacionCommandHandler(acreditaciones, documentos, alcance, proyectos, centros, unit)
                    .Handle(new(acreditacion.Id, EstadoAcreditacion.PendienteDeSubir, VigenciaEnPlataforma.SinConfirmar, version), CancellationToken.None)
            };

            if (alcanceCentro == "fuera")
            {
                resultado.EsFallido.Should().BeTrue(operacion);
                resultado.Error.Codigo.Should().Be("Acreditacion.NoEncontrada");
                unit.VecesGuardado.Should().Be(0);
            }
            else
            {
                resultado.EsExitoso.Should().BeTrue($"{operacion}, {alcanceCentro}");
                unit.VecesGuardado.Should().Be(1);
            }
        }
    }
}
