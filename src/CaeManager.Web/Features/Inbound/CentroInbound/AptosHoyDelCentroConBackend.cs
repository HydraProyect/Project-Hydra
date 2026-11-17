using CaeManager.Application.Inbound.ContratosAcceso.AptosHoy;
using CaeManager.Application.Inbound.ContratosAcceso.Queries.ObtenerContratosAcceso;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Inbound.ContratosAcceso;
using CaeManager.Web.Features.Inbound.ContratosAcceso.Recursos;
using CaeManager.Web.Features.Inbound.ContratosAcceso.Servicios;
using MediatR;
using Microsoft.Extensions.Localization;

namespace CaeManager.Web.Features.Inbound.CentroInbound;

/// <summary>
/// «Aptos hoy» del Centro con datos reales: la pantalla recibe lo que calcula <see cref="ObtenerAptosHoyDelCentroQuery"/> (estado
/// de hoy por Trabajador, con el requisito que bloquea o vence) y le pone nombre a la Empresa contratista con
/// <see cref="ResolutorContratosAcceso"/>. El DNI sale siempre enmascarado y no hay ningún campo con contenido clínico.
/// Si la lectura falla devuelve null, que la pantalla enseña como «no disponible» sin pintar datos.
/// </summary>
public sealed class AptosHoyDelCentroConBackend(
    IMediator mediator, ResolutorContratosAcceso resolutor, IStringLocalizer<TextosContratosAcceso> textos) : IAptosHoyDelCentro
{
    public async Task<IReadOnlyList<TrabajadorAptoHoy>?> ObtenerAsync(Guid centroId, CancellationToken cancellationToken = default)
    {
        try
        {
            var filas = await mediator.Send(new ObtenerAptosHoyDelCentroQuery(centroId), cancellationToken);
            if (filas.Count == 0)
                return [];

            // Solo se le pone nombre a las Empresas contratistas que salen en alguna fila: el resolutor las pide una a una.
            var necesarias = filas.Select(f => f.EmpresaContratistaId).ToHashSet();
            var contratos = (await mediator.Send(new ObtenerContratosAccesoQuery(centroId), cancellationToken))
                .Where(c => necesarias.Contains(c.EmpresaContratistaId))
                .ToList();
            var empresas = (await resolutor.ResolverAsync(contratos, cancellationToken))
                .GroupBy(f => f.Contrato.EmpresaContratistaId)
                .ToDictionary(g => g.Key, g => g.First().EmpresaNombre);

            return filas.Select(f => new TrabajadorAptoHoy(
                    f.TrabajadorId, f.Nombre, EnmascararDocumento(f.DocumentoIdentidad),
                    empresas.GetValueOrDefault(f.EmpresaContratistaId) ?? textos["EmpresaNoDisponible"].Value, TipoDe(f), EstadoDe(f.Estado),
                    f.Requisito, DetalleDe(f)))
                .ToList();
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private string TipoDe(AptoHoyDto fila) =>
        fila.TipoContrato is { } tipo ? textos[ContratoAccesoUi.ClaveTipo(tipo)].Value : textos["SinDato"].Value;

    private static EstadoAptitudHoy EstadoDe(EstadoAptitudInbound estado) => estado switch
    {
        EstadoAptitudInbound.Apto => EstadoAptitudHoy.Apto,
        EstadoAptitudInbound.EnTolerancia => EstadoAptitudHoy.EnTolerancia,
        _ => EstadoAptitudHoy.NoApto,
    };

    /// <summary>
    /// El detalle bajo el requisito: solo nombre de requisito y fechas, nunca el motivo de una conclusión médica. Un requisito
    /// que revela salud llega siempre como <see cref="CausaNoAptitudInbound.NoAcreditado"/> y sin fecha, y dice solo «Vencido»
    /// (decisión del 2026-11-13). La proyección restringida (sin <c>ValidarInbound</c>) no trae causa: no hay detalle. Un
    /// requisito de la Empresa contratista lo dice, porque deja fuera a todos sus Trabajadores.
    /// </summary>
    private string? DetalleDe(AptoHoyDto fila)
    {
        var deLaEmpresa = fila.SujetoDelRequisito == SujetoDelRequisitoInbound.EmpresaContratista;
        var cuando = fila.FechaVencimiento is { } fecha ? MascaraFecha.Formatear(fecha) : null;
        var detalle = fila.Estado switch
        {
            EstadoAptitudInbound.Apto => cuando is null ? null : textos["AptosSiguienteVencimiento", cuando].Value,
            EstadoAptitudInbound.EnTolerancia => cuando is null
                ? textos["AptosTolerancia"].Value : textos["AptosToleranciaEl", cuando].Value,
            _ => fila.Causa switch
            {
                null => null,
                CausaNoAptitudInbound.Vencido => cuando is null ? textos["AptosVencido"].Value : textos["AptosVencidoEl", cuando].Value,
                CausaNoAptitudInbound.SinFechaDeVencimiento => textos["AptosSinFechaVencimiento"].Value,
                CausaNoAptitudInbound.NoAcreditado => textos["AptosVencido"].Value,
                _ => textos["AptosSinDocumento"].Value,
            },
        };

        if (detalle is null)
            return null;

        return deLaEmpresa && fila.Estado != EstadoAptitudInbound.Apto
            ? textos["AptosRequisitoDeLaEmpresa", detalle].Value
            : detalle;
    }

    // El mismo enmascarado que aplica la Query en la proyección restringida; aplicarlo dos veces da lo mismo.
    private static string EnmascararDocumento(string? documento) => DniParcialAptosHoy.Enmascarar(documento);
}
