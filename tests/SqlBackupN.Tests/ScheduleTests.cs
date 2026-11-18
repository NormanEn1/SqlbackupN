using SqlBackupN.Core.Abstractions;
using SqlBackupN.Core.Domain;
using SqlBackupN.Core.Services;
using SqlBackupN.Presentation.Jobs;

namespace SqlBackupN.Tests;

/// <summary>Conversión entre el horario "amigable" y la expresión cron, y su descripción en palabras.</summary>
public class ScheduleBuilderTests
{
    [Theory]
    [InlineData("0 23 * * *")]
    [InlineData("0 22 * * 0")]
    [InlineData("0 22 * * 1-6")]
    [InlineData("0 8 * * 1-5")]
    [InlineData("0 9 * * 1,3,5")]
    [InlineData("0 10 * * 0,6")]
    [InlineData("*/15 * * * *")]
    [InlineData("* * * * *")]
    [InlineData("30 * * * *")]
    [InlineData("5 */6 * * *")]
    [InlineData("15 */2 * * 1-5")]
    [InlineData("*/30 * * * 1-5")]
    [InlineData("30 2 1 * *")]
    public void Friendly_schedules_round_trip_to_the_same_cron(string cron)
    {
        var spec = ScheduleBuilder.Parse(cron);

        Assert.NotEqual(ScheduleMode.Custom, spec.Mode);
        Assert.Equal(cron, ScheduleBuilder.ToCron(spec));
    }

    [Theory]
    [InlineData("0 0 1 1 *")]        // mes concreto
    [InlineData("*/7 * * * *")]      // intervalo que no divide la hora
    [InlineData("0 */5 * * *")]      // intervalo que no divide el día
    [InlineData("0 9-17 * * 1-5")]   // rango de horas
    [InlineData("0 22 * * MON")]     // nombres de día
    [InlineData("0 22 1 * 1")]       // día del mes y de la semana a la vez
    [InlineData("61 * * * *")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("0 23 * *")]
    public void Anything_else_stays_a_custom_cron(string cron)
    {
        var spec = ScheduleBuilder.Parse(cron);

        Assert.Equal(ScheduleMode.Custom, spec.Mode);
        Assert.Equal(cron, ScheduleBuilder.ToCron(spec));
        Assert.Null(ScheduleBuilder.Describe(cron));
    }

    [Fact]
    public void Parse_extracts_time_and_days()
    {
        var spec = ScheduleBuilder.Parse("45 21 * * 1-6");

        Assert.Equal(ScheduleMode.Weekly, spec.Mode);
        Assert.Equal(21, spec.Hour);
        Assert.Equal(45, spec.Minute);
        Assert.DoesNotContain(DayOfWeek.Sunday, spec.Days!);
        Assert.Equal(6, spec.Days!.Count);
    }

    [Fact]
    public void Seven_is_also_sunday_in_the_day_of_week_field()
    {
        var spec = ScheduleBuilder.Parse("0 22 * * 7");

        Assert.Equal(ScheduleMode.Weekly, spec.Mode);
        Assert.Equal([DayOfWeek.Sunday], spec.Days!.ToArray());
        Assert.Equal("0 22 * * 0", ScheduleBuilder.ToCron(spec));   // se normaliza a 0
    }

    [Fact]
    public void All_seven_days_is_just_daily()
    {
        Assert.Equal(ScheduleMode.Daily, ScheduleBuilder.Parse("0 22 * * 0-6").Mode);
        Assert.Equal(ScheduleMode.Daily, ScheduleBuilder.Parse("0 22 * * 0,1,2,3,4,5,6").Mode);
    }

    [Theory]
    [InlineData(ScheduleMode.Weekly)]
    [InlineData(ScheduleMode.EveryHours)]
    [InlineData(ScheduleMode.EveryMinutes)]
    public void A_schedule_by_days_with_no_days_has_no_cron(ScheduleMode mode)
    {
        var spec = new ScheduleSpec(mode, Days: new HashSet<DayOfWeek>());

        Assert.Null(ScheduleBuilder.ToCron(spec));
    }

    [Fact]
    public void Out_of_range_values_are_clamped_never_producing_an_invalid_cron()
    {
        var cron = ScheduleBuilder.ToCron(new ScheduleSpec(ScheduleMode.Daily, Hour: 99, Minute: -5));

        Assert.Equal("0 23 * * *", cron);
    }

    [Theory]
    [InlineData("0 23 * * *", "Todos los días a las 23:00")]
    [InlineData("0 22 * * 0", "Los domingos a las 22:00")]
    [InlineData("0 22 * * 1-6", "De lunes a sábado a las 22:00")]
    [InlineData("0 8 * * 1-5", "De lunes a viernes a las 08:00")]
    [InlineData("0 10 * * 0,6", "Sábados y domingos a las 10:00")]
    [InlineData("0 9 * * 1,3,5", "Los lunes, miércoles y viernes a las 09:00")]
    [InlineData("0 9 * * 1,2", "Los lunes y martes a las 09:00")]
    [InlineData("0 20 * * 5-7", "De viernes a domingo a las 20:00")]
    [InlineData("*/15 * * * *", "Cada 15 minutos")]
    [InlineData("* * * * *", "Cada minuto")]
    [InlineData("0 * * * *", "Cada hora, en el minuto 00")]
    [InlineData("5 */6 * * *", "Cada 6 horas, en el minuto 05")]
    [InlineData("*/30 * * * 1-5", "Cada 30 minutos · de lunes a viernes")]
    [InlineData("30 2 1 * *", "El día 1 de cada mes a las 02:30")]
    public void Schedules_are_described_in_plain_spanish(string cron, string expected)
    {
        Assert.Equal(expected, ScheduleBuilder.Describe(cron));
    }

    [Fact]
    public void Every_generated_cron_is_accepted_by_the_validator()
    {
        foreach (var hours in ScheduleBuilder.HourIntervals)
        {
            var cron = ScheduleBuilder.ToCron(new ScheduleSpec(ScheduleMode.EveryHours, Minute: 10, IntervalHours: hours))!;
            Assert.NotNull(ScheduleEvaluator.NextOccurrences(cron, DateTime.UtcNow, TimeZoneInfo.Utc, 1).Cast<DateTime?>().FirstOrDefault());
        }

        foreach (var minutes in ScheduleBuilder.MinuteIntervals)
        {
            var cron = ScheduleBuilder.ToCron(new ScheduleSpec(ScheduleMode.EveryMinutes, IntervalMinutes: minutes))!;
            Assert.NotEmpty(ScheduleEvaluator.NextOccurrences(cron, DateTime.UtcNow, TimeZoneInfo.Utc, 1));
        }
    }
}

/// <summary>Comportamiento del paso de programación en el editor (frecuencia, hora, días).</summary>
public class StepEditorViewModelTests
{
    private static (StepEditorViewModel Step, Func<int> Changes) Create(string cron)
    {
        var changes = 0;
        var step = new StepEditorViewModel(new BackupStep { Type = BackupType.Full, Cron = cron }, () => changes++, _ => { });
        return (step, () => changes);
    }

    [Fact]
    public void An_existing_cron_is_shown_as_friendly_controls()
    {
        var (step, _) = Create("0 22 * * 1-6");

        Assert.Equal(ScheduleMode.Weekly, step.Mode);
        Assert.Equal(22, step.Hour);
        Assert.Equal(0, step.Minute);
        Assert.True(step.Monday);
        Assert.True(step.Saturday);
        Assert.False(step.Sunday);
        Assert.Equal("De lunes a sábado a las 22:00", step.Summary);
        Assert.True(step.ShowTime);
        Assert.True(step.ShowDays);
        Assert.False(step.ShowCron);
        Assert.Equal("cron: 0 22 * * 1-6", step.CronText);
    }

    [Fact]
    public void Changing_the_hour_or_minute_regenerates_the_cron()
    {
        var (step, changes) = Create("0 22 * * 1-6");

        step.Hour = 5;
        step.Minute = 30;

        Assert.Equal("30 5 * * 1-6", step.Cron);
        Assert.Equal("De lunes a sábado a las 05:30", step.Summary);
        Assert.Equal(2, changes());
    }

    [Fact]
    public void Toggling_a_day_regenerates_the_cron()
    {
        var (step, _) = Create("0 22 * * 1-6");

        step.Monday = false;
        Assert.Equal("0 22 * * 2-6", step.Cron);

        step.Sunday = true;
        Assert.Equal("0 22 * * 0,2-6", step.Cron);
    }

    [Fact]
    public void Unchecking_every_day_is_flagged_and_keeps_the_last_valid_cron()
    {
        var (step, _) = Create("0 22 * * 1,2");

        step.Monday = false;
        step.Tuesday = false;

        Assert.True(step.NoDays);
        Assert.Equal("Marque al menos un día de la semana.", step.Summary);
        Assert.Equal("0 22 * * 2", step.Cron);   // el último cron válido, nunca uno roto
    }

    [Fact]
    public void Switching_frequency_builds_the_matching_cron()
    {
        var (step, _) = Create("0 23 * * *");
        Assert.Equal(ScheduleMode.Daily, step.Mode);

        step.Mode = ScheduleMode.EveryMinutes;
        Assert.Equal("*/15 * * * *", step.Cron);
        Assert.True(step.ShowMinuteInterval);
        Assert.False(step.ShowTime);

        step.IntervalMinutes = 5;
        Assert.Equal("*/5 * * * *", step.Cron);

        step.Mode = ScheduleMode.EveryHours;
        step.IntervalHours = 6;
        Assert.Equal("0 */6 * * *", step.Cron);
        Assert.True(step.ShowHourInterval);

        step.Mode = ScheduleMode.Monthly;
        step.DayOfMonth = 15;
        Assert.Equal("0 23 15 * *", step.Cron);
        Assert.True(step.ShowMonthDay);

        step.Mode = ScheduleMode.Daily;
        Assert.Equal("0 23 * * *", step.Cron);
    }

    [Fact]
    public void A_cron_that_does_not_fit_opens_in_advanced_mode_and_stays_editable()
    {
        var (step, _) = Create("0 9-17 * * 1-5");

        Assert.Equal(ScheduleMode.Custom, step.Mode);
        Assert.True(step.ShowCron);
        Assert.False(step.ShowCronText);
        Assert.Equal("Expresión cron personalizada", step.Summary);

        step.Cron = "0 10-18 * * 1-5";
        Assert.Equal(ScheduleMode.Custom, step.Mode);
        Assert.Equal("0 10-18 * * 1-5", step.Cron);
    }

    [Fact]
    public void Typing_a_cron_by_hand_switches_a_friendly_step_to_advanced_mode()
    {
        var (step, _) = Create("0 23 * * *");

        step.Cron = "abc";

        Assert.Equal(ScheduleMode.Custom, step.Mode);
        Assert.True(step.ShowCron);
    }

    [Fact]
    public void Leaving_advanced_mode_reuses_what_can_be_understood_from_the_cron()
    {
        var (step, _) = Create("0 9-17 * * *");
        step.Cron = "30 4 * * 1-5";

        step.Mode = ScheduleMode.Weekly;

        Assert.Equal(4, step.Hour);
        Assert.Equal(30, step.Minute);
        Assert.False(step.Saturday);
        Assert.Equal("30 4 * * 1-5", step.Cron);
    }

    [Fact]
    public void Values_are_clamped_to_valid_ranges()
    {
        var (step, _) = Create("0 23 * * *");

        step.Hour = 99;
        step.Minute = -3;

        Assert.Equal(23, step.Hour);
        Assert.Equal(0, step.Minute);
        Assert.Equal("0 23 * * *", step.Cron);
    }

    [Fact]
    public void ToStep_keeps_the_identifier_and_the_generated_cron()
    {
        var original = new BackupStep { Type = BackupType.Differential, Cron = "0 22 * * 1-6" };
        var step = new StepEditorViewModel(original, () => { }, _ => { });

        step.Hour = 21;
        var result = step.ToStep();

        Assert.Equal(original.Id, result.Id);
        Assert.Equal(BackupType.Differential, result.Type);
        Assert.Equal("0 21 * * 1-6", result.Cron);
    }

    [Fact]
    public void A_step_without_days_blocks_saving_in_the_editor_with_a_clear_message()
    {
        var job = new BackupJob
        {
            Name = "Ventas",
            Connection = new SqlServerConnection { Server = "srv01" },
            Steps = [new BackupStep { Type = BackupType.Full, Cron = "0 22 * * 1" }],
            Destinations = [new DestinationConfig { Name = "Local", Path = @"D:\Backups" }],
            Notifications = new NotificationSettings { WebhookUrl = "https://hooks.example.test/x" }
        };
        var services = new EditorServices(new StubProtector(), new StubExecutor(), new StubFiles(), new StubRemote(),
            new StubAuthorizer(), new StubGoogleClients(), new StubDialogs(), TimeProvider.System);
        var editor = new JobEditorViewModel(JobFactory.Clone(job), false, [], services);
        Assert.True(editor.CanSave);

        editor.Steps[0].Monday = false;

        Assert.False(editor.CanSave);
        Assert.Contains(editor.Issues, i => i.IsMissing && i.Text == "Programación (paso 1): Marque al menos un día de la semana.");
        Assert.Equal("Marque al menos un día de la semana.", editor.Steps[0].Summary);
    }
}
