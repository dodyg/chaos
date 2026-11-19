import React from "react";
import {
  Bell,
  BookOpen,
  Bookmark,
  Briefcase,
  Bug,
  Calendar,
  Clock,
  Coffee,
  Flag,
  Folder,
  GitBranch,
  Heart,
  House,
  Inbox,
  Layers,
  ListTodo,
  Moon,
  Rocket,
  SquareCheck,
  Star,
  Sun,
  Tag,
  Target,
  User,
  Users,
  Wrench,
  Zap,
} from "lucide-react";

/** The fixed icon set a custom view can use (PLAN2 §11.2); names are the host's validated list. */
const icons: Record<string, React.ComponentType<{ className?: string }>> = {
  list: ListTodo,
  star: Star,
  flag: Flag,
  bug: Bug,
  calendar: Calendar,
  clock: Clock,
  inbox: Inbox,
  briefcase: Briefcase,
  home: House,
  user: User,
  users: Users,
  tag: Tag,
  bookmark: Bookmark,
  heart: Heart,
  target: Target,
  zap: Zap,
  bell: Bell,
  "square-check": SquareCheck,
  folder: Folder,
  "git-branch": GitBranch,
  github: GitBranch,
  "book-open": BookOpen,
  coffee: Coffee,
  sun: Sun,
  moon: Moon,
  layers: Layers,
  rocket: Rocket,
  wrench: Wrench,
};

const colors: Record<string, string> = {
  slate: "text-slate-500",
  red: "text-red-600",
  orange: "text-orange-500",
  amber: "text-amber-500",
  green: "text-green-600",
  teal: "text-teal-600",
  blue: "text-blue-600",
  violet: "text-violet-600",
  pink: "text-pink-600",
};

export function ViewIcon({ name, color }: { name: string; color: string | null }) {
  const Icon = icons[name] ?? ListTodo;
  return <Icon className={color ? colors[color] : undefined} />;
}
