using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;
using DiscordChatExporter.Core.Exceptions;
using Gress;

namespace DiscordChatExporter.Core.Exporting;

public class ChannelExporter(DiscordClient discord)
{
    public async ValueTask ExportChannelAsync(
        ExportRequest request,
        ConcurrentDictionary<Snowflake, Channel> channelsById,
        ConcurrentDictionary<Snowflake, Role> rolesById,
        IProgress<Percentage>? progress = null,
        CancellationToken cancellationToken = default
    )
    {
        // Forum channels don't have messages, they are just a list of threads
        if (request.Channel.Kind == ChannelKind.GuildForum)
        {
            throw new DiscordChatExporterException(
                $"Channel '{request.Channel.Name}' "
                    + $"of guild '{request.Guild.Name}' "
                    + $"is a forum and cannot be exported directly. "
                    + "You need to pull its threads and export them individually."
            );
        }

        // custom code: pre-check against forbidden channels to prevent them from being written
        if (!request.Channel.IsThread)
        {
            try
            {
                var channel = await discord.GetChannelAsync(request.Channel.Id, cancellationToken);
            }
            catch (Exception ex)
            {
                // Provide more context to the exception, to simplify debugging based on error messages
                throw new DiscordChatExporterException(
                    $"Failed to export channel '{request.Channel.Name}' (#{request.Channel.Id}) "
                        + $"of guild '{request.Guild.Name} (#{request.Guild.Id})'.",
                    ex is not DiscordChatExporterException dex || dex.IsFatal,
                    ex
                );
            }
        }

        // Build context
        var context = new ExportContext(discord, request);
        context.PopulateChannelsAndRoles(channelsById, rolesById);

        // Initialize the exporter before further checks to ensure the file is created even if
        // an exception is thrown after this point.
        await using var messageExporter = new MessageExporter(context);

        if (request.MessageLimit == 0)
            return;

        // Check if the channel is empty
        if (request.Channel.IsEmpty)
        {
            throw new ChannelEmptyException(
                $"Channel '{request.Channel.Name}' "
                    + $"of guild '{request.Guild.Name}' "
                    + $"does not contain any messages; an empty file will be created."
            );
        }

        // Check if the 'before' and 'after' boundaries are valid
        if (
            (
                request.Before is not null
                && !request.Channel.MayHaveMessagesBefore(request.Before.Value)
            )
            || (
                request.After is not null
                && !request.Channel.MayHaveMessagesAfter(request.After.Value)
            )
        )
        {
            throw new ChannelEmptyException(
                $"Channel '{request.Channel.Name}' "
                    + $"of guild '{request.Guild.Name}' "
                    + $"does not contain any messages within the specified period; an empty file will be created."
            );
        }

        int messages_saved = 0;
        await foreach (
            var message in discord.GetMessagesAsync(
                request.Channel.Id,
                request.After,
                request.Before,
                progress,
                cancellationToken
            )
        )
        {
            if (request.MessageLimit >= 0 && messages_saved >= request.MessageLimit)
                break;
            try
            {
                // Resolve members for referenced users
                foreach (var user in message.GetReferencedUsers())
                    await context.PopulateMemberAsync(user, cancellationToken);

                // Export the message
                if (request.MessageFilter.IsMatch(message))
                {
                    await messageExporter.ExportMessageAsync(message, cancellationToken);
                    messages_saved++;
                }
            }
            catch (Exception ex)
            {
                // Provide more context to the exception, to simplify debugging based on error messages
                throw new DiscordChatExporterException(
                    $"Failed to export message #{message.Id} "
                        + $"in channel '{request.Channel.Name}' (#{request.Channel.Id}) "
                        + $"of guild '{request.Guild.Name} (#{request.Guild.Id})'.",
                    ex is not DiscordChatExporterException dex || dex.IsFatal,
                    ex
                );
            }
        }
    }
}
