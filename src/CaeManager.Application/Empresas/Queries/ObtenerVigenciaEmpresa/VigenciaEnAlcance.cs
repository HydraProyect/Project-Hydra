using CaeManager.Domain.Documentos;

namespace CaeManager.Application.Empresas.Queries.ObtenerVigenciaEmpresa;

/// <summary>
/// La celda de la matriz «Vigencia» vista desde un alcance de Centros (todos los del Trabajador, o el que elige el filtro de
/// Centro). Es una regla de lectura sin I/O, para que la pantalla y los tests compartan la misma:
/// <list type="bullet">
/// <item>Exigida en el alcance = algún Centro del alcance que lo exige a este Trabajador.</item>
/// <item>Sin documento: <see cref="EstadoCeldaVigencia.Falta"/> si se exige, <see cref="EstadoCeldaVigencia.NoAplica"/> si no.</item>
/// <item>Con documento que ningún Centro del alcance exige: opcional (<see cref="Exigida"/> falso), con su propia fecha.</item>
/// <item>Con la vigencia sin confirmar: <see cref="EstadoCeldaVigencia.SinConfirmar"/>.</item>
/// <item>En otro caso, el vencimiento efectivo de <see cref="ReglaBloqueoDeAcceso.VencimientoEfectivo"/> (la misma regla que decide el
/// acceso, para que la vista y el bloqueo no diverjan): si el Centro define una periodicidad especial, vence en emisión + esos meses
/// y SUSTITUYE a la fecha del documento; si no, vale la del documento; «no caduca» nunca vence. Con varios Centros en el alcance manda
/// el vencimiento más temprano. Vencida si ya pasó, «por vencer» dentro del umbral de Parámetros del sistema.</item>
/// </list>
/// </summary>
public static class VigenciaEnAlcance
{
    public readonly record struct Resultado(EstadoCeldaVigencia Estado, DateOnly? Fecha, bool Exigida, int? VigenciaMinimaMeses);

    /// <param name="alcance">Los Centros que se miran; <c>null</c> = todos los del Trabajador.</param>
    public static Resultado Resolver(
        VigenciaColumnaDto columna, VigenciaCeldaDto celda, IReadOnlyCollection<Guid>? alcance, DateOnly hoy, int umbralPorVencerDias)
    {
        var exigidaPor = alcance is null
            ? celda.ExigidaPorCentroIds
            : [.. celda.ExigidaPorCentroIds.Where(alcance.Contains)];
        var exigida = exigidaPor.Count > 0;

        var meses = columna.ExigidoPor
            .Where(e => exigidaPor.Contains(e.CentroId) && e.VigenciaMeses is not null)
            .Select(e => e.VigenciaMeses!.Value)
            .DefaultIfEmpty()
            .Min();
        int? vigenciaMinima = meses > 0 ? meses : null;

        if (celda.DocumentoId is null)
            return new(exigida ? EstadoCeldaVigencia.Falta : EstadoCeldaVigencia.NoAplica, null, exigida, vigenciaMinima);

        if (!exigida)
            return new(EstadoCeldaVigencia.Vigente, celda.FechaVencimiento, false, vigenciaMinima);

        // El vencimiento de cada Centro del alcance con SU regla (periodicidad especial del Centro o fecha del documento).
        var vigenciaDelDocumento = celda.Estado == EstadoCeldaVigencia.SinConfirmar ? VigenciaDocumento.SinConfirmar
            : celda.FechaVencimiento is { } f ? VigenciaDocumento.VenceEl(f)
            : VigenciaDocumento.NoCaduca;
        var emision = celda.FechaEmision ?? hoy;
        var vencimientos = columna.ExigidoPor
            .Where(e => exigidaPor.Contains(e.CentroId))
            .Select(e => ReglaBloqueoDeAcceso.VencimientoEfectivo(new DocumentoParaAcceso(vigenciaDelDocumento, emision), e.PeriodicidadEspecialMeses))
            .OfType<DateOnly>()
            .ToList();

        // Ningún Centro le pone fecha: sin confirmar sigue sin confirmar; confirmado que no caduca, vigente.
        if (vencimientos.Count == 0)
            return vigenciaDelDocumento.Estado == EstadoVigenciaDocumento.SinConfirmar
                ? new(EstadoCeldaVigencia.SinConfirmar, null, true, vigenciaMinima)
                : new(EstadoCeldaVigencia.Vigente, null, true, vigenciaMinima);

        var fecha = vencimientos.Min();
        var dias = fecha.DayNumber - hoy.DayNumber;
        var estado = dias < 0 ? EstadoCeldaVigencia.Vencido
            : dias <= umbralPorVencerDias ? EstadoCeldaVigencia.PorVencer
            : EstadoCeldaVigencia.Vigente;
        return new(estado, fecha, true, vigenciaMinima);
    }
}
