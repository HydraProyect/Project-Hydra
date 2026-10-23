using CaeManager.Application.Common;
using FluentValidation;
using Microsoft.Extensions.Options;

namespace CaeManager.Application.AsistenteIa.Queries.PreguntarAlAsistente;

public class PreguntarAlAsistenteQueryValidator : AbstractValidator<PreguntarAlAsistenteQuery>
{
    public PreguntarAlAsistenteQueryValidator(IOptions<AsistenteConsultaOptions> opciones)
    {
        RuleFor(q => q.Historial).NotEmpty().WithMessage(_ => TextosConsulta.Obtener("PreguntaInvalida"));
        When(q => q.Historial is { Count: > 0 }, () =>
        {
            RuleFor(q => q.Historial[q.Historial.Count - 1].Rol).Equal(RolMensajeChat.Usuario)
                .WithMessage(_ => TextosConsulta.Obtener("PreguntaInvalida"));
            RuleFor(q => q.Historial[q.Historial.Count - 1].Texto).NotEmpty()
                .WithMessage(_ => TextosConsulta.Obtener("PreguntaInvalida"))
                .MaximumLength(opciones.Value.MaxCaracteresPregunta)
                .WithMessage(_ => string.Format(TextosConsulta.Obtener("PreguntaDemasiadoLarga"), opciones.Value.MaxCaracteresPregunta));
        });
    }
}
