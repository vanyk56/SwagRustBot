import 'dotenv/config';
import {REST,Routes,SlashCommandBuilder,PermissionFlagsBits} from 'discord.js';
const commands=[
 new SlashCommandBuilder().setName('setup').setDescription('Закрепить меню SwagRust').setDefaultMemberPermissions(PermissionFlagsBits.ManageGuild),
 new SlashCommandBuilder().setName('setup-ideas').setDescription('Закрепить панель предложений в этом канале').setDefaultMemberPermissions(PermissionFlagsBits.ManageGuild),
 new SlashCommandBuilder().setName('setup-ideas-channel').setDescription('Назначить канал, куда будут поступать идеи').setDefaultMemberPermissions(PermissionFlagsBits.ManageGuild),
 new SlashCommandBuilder().setName('setup-info').setDescription('Закрепить информацию о сервере').setDefaultMemberPermissions(PermissionFlagsBits.ManageGuild),
 new SlashCommandBuilder().setName('status').setDescription('Статус сервера SwagRust'),
 new SlashCommandBuilder().setName('link').setDescription('Привязать Steam ID').addStringOption(o=>o.setName('code').setDescription('Код /link из игры').setRequired(true)),
 new SlashCommandBuilder().setName('stats').setDescription('Статистика игрока').addUserOption(o=>o.setName('user').setDescription('Discord-пользователь')),
 new SlashCommandBuilder().setName('top').setDescription('Таблица лидеров').addStringOption(o=>o.setName('category').setDescription('Категория').setRequired(true).addChoices({name:'Убийства',value:'kills'},{name:'K/D',value:'kd'},{name:'Онлайн',value:'playtime'},{name:'Фарм',value:'farm'},{name:'Рейды',value:'raids'},{name:'Очки',value:'score'})),
 new SlashCommandBuilder().setName('idea').setDescription('Предложить идею').addStringOption(o=>o.setName('text').setDescription('Опишите идею').setRequired(true).setMaxLength(1000))
].map(x=>x.toJSON());
if(!process.env.DISCORD_TOKEN||!process.env.DISCORD_CLIENT_ID)throw new Error('Заполните DISCORD_TOKEN и DISCORD_CLIENT_ID');
const rest=new REST({version:'10'}).setToken(process.env.DISCORD_TOKEN);
const route=process.env.DISCORD_GUILD_ID?Routes.applicationGuildCommands(process.env.DISCORD_CLIENT_ID,process.env.DISCORD_GUILD_ID):Routes.applicationCommands(process.env.DISCORD_CLIENT_ID);
await rest.put(route,{body:commands}); console.log(`Зарегистрировано команд: ${commands.length}`);
